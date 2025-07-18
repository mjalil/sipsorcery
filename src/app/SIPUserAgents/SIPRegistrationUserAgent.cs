﻿// ============================================================================
// FileName: SIPRegistrationUserAgent.cs
//
// Description:
// A user agent that can register and maintain a binding with a SIP Registrar.
//
// Author(s):
// Aaron Clauson (aaron@sipsorcery.com)
//
// History:
// 03 Mar 2010	Aaron Clauson	Created, Hobart, Australia.
// rj2: some PBX/Trunks need UserDisplayName in SIP-REGISTER
//
// License:
// BSD 3-Clause "New" or "Revised" License, see included LICENSE.md file.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SIPSorcery.Sys;

namespace SIPSorcery.SIP.App
{
    public class SIPRegistrationUserAgent
    {
        private const int MAX_EXPIRY = 7200;
        private const int REGISTRATION_HEAD_TIME = 5;                // Time in seconds to go to next registration to initiate.
        //rj2: there are PBX which send new Expires header in SIP OK with value lesser than 60 -> set hardcoded minimum to 10, so registration on PBX does not timeout
        private const int REGISTER_MINIMUM_EXPIRY = 10;              // The minimum interval a registration will be accepted for. Anything less than this interval will use this minimum value.
        private const int DEFAULT_REGISTER_EXPIRY = 600;
        private const int DEFAULT_MAX_REGISTRATION_ATTEMPT_TIMEOUT = 60;
        private const int DEFAULT_REGISTER_FAILURE_RETRY_INTERVAL = 300;
        private const int DEFAULT_MAX_REGISTER_ATTEMPTS = 3;

        private static ILogger logger = Log.Logger;

        private SIPTransport m_sipTransport;
        private SIPEndPoint m_outboundProxy;
        private SIPURI m_sipAccountAOR;
        private string m_authUsername;
        private string m_password;
        private string m_realm;
        private string m_registrarHost;
        private SIPURI m_contactURI;
        private long m_expiry;
        private long m_originalExpiry;
        private int m_registerFailureRetryInterval;     // Number of seconds between consecutive register requests in the event of failures or timeouts.
        private int m_maxRegistrationAttemptTimeout;    // The period in seconds to wait for a server response before classifying the registration request as failed.
        private int m_maxRegisterAttempts;              // The maximum number of registration attempts that will be made without a failure condition before incurring a temporary failure.
        private bool m_exitOnUnequivocalFailure;        // If true the agent will exit on failure conditions that most likely require manual intervention.
        private string m_overridenAllowHeaderValue;

        private bool m_isRegistered;
        private int m_cseq;
        private string m_callID;
        private string[] m_customHeaders;
        private bool m_exit;
        private int m_attempts;
        private ManualResetEvent m_waitForRegistrationMRE = new ManualResetEvent(false);
        private Timer m_registrationTimer;

        public string UserAgent;                // If not null this value will replace the default user agent value in the REGISTER request.
        public string UserDisplayName;			//rj2: if not null, used in fromheader and contactheader

        /// <summary>
        /// True if the last registration attempt was successful or false if not.
        /// </summary>
        public bool IsRegistered
        {
            get { return m_isRegistered; }
        }

        /// <summary>
        /// The last time at which an attempt was made to register this account.
        /// </summary>
        public DateTime LastRegisterAttemptAt { get; private set; }

        public event Action<SIPURI, SIPResponse, string> RegistrationFailed;
        public event Action<SIPURI, SIPResponse, string> RegistrationTemporaryFailure;
        public event Action<SIPURI, SIPResponse> RegistrationSuccessful;
        public event Action<SIPURI, SIPResponse> RegistrationRemoved;

        public Func<SIPRequest, SIPRequest> AdjustRegister;
        public Func<long, int> AdjustRefreshTime;

        /// <summary>
        /// If set all requests will be sent via the outbound SIP proxy instead of being sent to the
        /// SIP registrar server.
        /// </summary>
        public SIPEndPoint OutboundProxy
        {
            get { return m_outboundProxy; }
            set { m_outboundProxy = value; }
        }

        /// <summary>
        /// Creates a new SIP registration agent that will attempt to register with a SIP Registrar server.
        /// If the registration fails the agent will retry up to a hard coded maximum number of 3 attempts.
        /// If successful the agent will periodically refresh the registration based on the Expiry time 
        /// returned by the server.
        /// </summary>
        /// <param name="sipTransport">The SIP transport layer to use to send the register request.</param>
        /// <param name="username">The username to use if the server requests authorisation.</param>
        /// <param name="password">The password to use if the server requests authorisation.</param>
        /// <param name="server">The hostname or socket address for the registration server. Can be in a format of
        /// hostname:port or ipaddress:port, e.g. sipsorcery.com or 67.222.131.147:5060. The transport can also
        /// be specified using a SIP URI parameter, e.g. sip:sipsorcery.com;transport=tcp or sip:sipsorcery.com;transport=tls
        /// although in the latter case it would be better to use sips:sipsorcery.com.</param>
        /// <param name="expiry">The expiry value to request for the contact. This value can be rejected or overridden
        /// by the server.</param>
        /// <param name="maxRegistrationAttemptTimeout">The period in seconds to wait for a server response before
        /// classifying the registration request as failed.</param>
        /// <param name="registerFailureRetryInterval">Number of seconds between consecutive register requests in the 
        /// event of failures or timeouts.</param>
        /// <param name="maxRegisterAttempts">The maximum number of registration attempts that will be made without a 
        /// failure condition before incurring a temporary failure.</param>
        /// <param name="exitOnUnequivocalFailure">If true the agent will exit on failure conditions that most 
        /// likely require manual intervention. It is recommended to leave this as true.</param>
        /// <param name="sendUsernameInContactHeader">If true the request will add the username to the Contact header.</param>
        public SIPRegistrationUserAgent(
            SIPTransport sipTransport,
            string username,
            string password,
            string server,
            int expiry,
            int maxRegistrationAttemptTimeout = DEFAULT_MAX_REGISTRATION_ATTEMPT_TIMEOUT,
            int registerFailureRetryInterval = DEFAULT_REGISTER_FAILURE_RETRY_INTERVAL,
            int maxRegisterAttempts = DEFAULT_MAX_REGISTER_ATTEMPTS,
            bool exitOnUnequivocalFailure = true,
            bool sendUsernameInContactHeader = false)
        {
            m_sipTransport = sipTransport;

            if (SIPURI.TryParse(server, out var serverUri))
            {
                m_sipAccountAOR = serverUri;
                m_sipAccountAOR.User = username;
            }
            else
            {
                m_sipAccountAOR = new SIPURI(username, server, null, SIPSchemesEnum.sip, SIPProtocolsEnum.udp);
            }

            m_authUsername = username;
            m_password = password;
            m_registrarHost = server;
            m_expiry = (expiry >= REGISTER_MINIMUM_EXPIRY && expiry <= MAX_EXPIRY) ? expiry : DEFAULT_REGISTER_EXPIRY;
            m_originalExpiry = m_expiry;
            m_callID = Guid.NewGuid().ToString();
            m_maxRegistrationAttemptTimeout = maxRegistrationAttemptTimeout;
            m_registerFailureRetryInterval = registerFailureRetryInterval;
            m_maxRegisterAttempts = maxRegisterAttempts;
            m_exitOnUnequivocalFailure = exitOnUnequivocalFailure;
            m_exit = true;

            // Setting the contact to "0.0.0.0" tells the transport layer to populate it at send time.
            m_contactURI = new SIPURI(m_sipAccountAOR.Scheme, IPAddress.Any, 0);
            if (sendUsernameInContactHeader)
            {
                m_contactURI.User = username;
            }
        }

        public SIPRegistrationUserAgent(
            SIPTransport sipTransport,
            SIPEndPoint outboundProxy,
            SIPURI sipAccountAOR,
            string authUsername,
            string password,
            string realm,
            string registrarHost,
            SIPURI contactURI,
            int expiry,
            string[] customHeaders,
            int maxRegistrationAttemptTimeout = DEFAULT_MAX_REGISTRATION_ATTEMPT_TIMEOUT,
            int registerFailureRetryInterval = DEFAULT_REGISTER_FAILURE_RETRY_INTERVAL,
            int maxRegisterAttempts = DEFAULT_MAX_REGISTER_ATTEMPTS,
            bool exitOnUnequivocalFailure = true)
        {
            m_sipTransport = sipTransport;
            m_outboundProxy = outboundProxy;
            m_sipAccountAOR = sipAccountAOR;
            m_authUsername = authUsername;
            m_password = password;
            m_realm = realm;
            m_registrarHost = registrarHost;
            m_contactURI = contactURI;
            m_expiry = (expiry >= REGISTER_MINIMUM_EXPIRY && expiry <= MAX_EXPIRY) ? expiry : DEFAULT_REGISTER_EXPIRY;
            m_originalExpiry = m_expiry;
            m_customHeaders = customHeaders;
            m_callID = CallProperties.CreateNewCallId();
            m_maxRegistrationAttemptTimeout = maxRegistrationAttemptTimeout;
            m_registerFailureRetryInterval = registerFailureRetryInterval;
            m_maxRegisterAttempts = maxRegisterAttempts;
            m_exitOnUnequivocalFailure = exitOnUnequivocalFailure;
            m_exit = true;
        }

        public void Start()
        {
            if (m_registrationTimer != null)
            {
                throw new ApplicationException("SIPRegistrationUserAgent is already running, try Stop() at first!");
            }

            m_expiry = m_originalExpiry;
            m_exit = false;
            long callbackPeriod = (m_expiry - REGISTRATION_HEAD_TIME) * 1000;
            logger.LogDebug("Starting SIPRegistrationUserAgent for {SIPAccountAOR}, callback period {CallbackPeriodSeconds}s.", m_sipAccountAOR, callbackPeriod / 1000);

            if (callbackPeriod < REGISTER_MINIMUM_EXPIRY * 1000)
            {
                m_registrationTimer = new Timer(DoRegistration);
                m_registrationTimer.Change(0, REGISTER_MINIMUM_EXPIRY * 1000);
            }
            else
            {
                m_registrationTimer = new Timer(DoRegistration);
                m_registrationTimer.Change(0, callbackPeriod);
            }
        }

        private void DoRegistration(object state)
        {
            if (Monitor.TryEnter(m_waitForRegistrationMRE))
            {
                try
                {
                    logger.LogDebug("Starting registration for {SIPAccountAOR}.", m_sipAccountAOR);

                    LastRegisterAttemptAt = DateTime.Now;
                    m_waitForRegistrationMRE.Reset();
                    m_attempts = 0;

                    SendInitialRegister();

                    if (!m_waitForRegistrationMRE.WaitOne(m_maxRegistrationAttemptTimeout * 1000))
                    {
                        m_isRegistered = false;

                        if (!m_exit && RegistrationTemporaryFailure != null)
                        {
                            RegistrationTemporaryFailure(m_sipAccountAOR, null, "Registration to " + m_registrarHost + " for " + m_sipAccountAOR.ToString() + " timed out.");
                        }
                    }

                    if (!m_exit)
                    {
                        if (m_isRegistered)
                        {
                            var refreshTime = AdjustRefreshTime?.Invoke(m_expiry) ?? (m_expiry - REGISTRATION_HEAD_TIME);

                            logger.LogDebug("SIPRegistrationUserAgent was successful, scheduling next registration to {SIPAccountAOR} in {RefreshTime}s.", m_sipAccountAOR, refreshTime);
                            m_registrationTimer.Change(refreshTime * 1000, Timeout.Infinite);
                        }
                        else
                        {
                            logger.LogDebug("SIPRegistrationUserAgent temporarily failed, scheduling next registration to {SIPAccountAOR} in {RegisterFailureRetryInterval}s.", m_sipAccountAOR, m_registerFailureRetryInterval);
                            m_registrationTimer.Change(m_registerFailureRetryInterval * 1000, Timeout.Infinite);
                        }
                    }
                }
                catch (Exception excp)
                {
                    logger.LogError(excp, "Exception DoRegistration Start.");
                }
                finally
                {
                    Monitor.Exit(m_waitForRegistrationMRE);
                }
            }
        }

        /// <summary>
        /// Allows to override the Allow header value for REGISTER requests
        /// </summary>
        /// <param name="newAllowHeaderValue">a new Allow header value; if the null is passed then the default Allow header value will be used</param>
        /// <exception cref="System.ApplicationException">if <paramref name="newAllowHeaderValue"/> contains the methods that are not allowed</exception>
        public void OverrideAllowHeader(string newAllowHeaderValue)
        {
            if (string.IsNullOrEmpty(newAllowHeaderValue))
            {
                m_overridenAllowHeaderValue = newAllowHeaderValue;
            }
            else
            {
                IEnumerable<string> splitMethodsString(string methodsString)
                    => methodsString.Split(',').Select(s => s.Trim());

                var allowedMethods = splitMethodsString(SIPConstants.ALLOWED_SIP_METHODS);
                var newAllowedMethods = splitMethodsString(newAllowHeaderValue);
                var notAllowedMethods = newAllowedMethods.Except(allowedMethods).ToList();
                if (notAllowedMethods.Count == 0)
                {
                    m_overridenAllowHeaderValue = newAllowHeaderValue;
                }
                else
                {
                    throw new ApplicationException(
                        $"The following methods are not allowed: {string.Join(", ", notAllowedMethods)}");
                }
            }
        }

        /// <summary>
        /// Allows the registration expiry setting to be adjusted after the instance has been created.
        /// </summary>
        /// <param name="expiry">The new expiry value.</param>
        public void SetExpiry(int expiry)
        {
            int newExpiry = (expiry >= REGISTER_MINIMUM_EXPIRY && expiry <= MAX_EXPIRY) ? expiry : DEFAULT_REGISTER_EXPIRY;

            if (newExpiry != m_expiry)
            {
                logger.LogInformation("Expiry for registration agent for {SIPAccountAOR} updated from {OldExpiry} to {NewExpiry}.", m_sipAccountAOR, m_expiry, newExpiry);

                m_expiry = newExpiry;

                // Schedule an immediate registration for the new value.
                m_registrationTimer.Change(0, Timeout.Infinite);
            }
        }

        /// <summary>
        /// Stops the registration agent.
        /// </summary>
        /// <param name="sendZeroExpiryRegister">If true a final registration with a zero expiry
        /// will be sent to remove the binding.</param>
        public void Stop(bool sendZeroExpiryRegister = true)
        {
            try
            {
                logger.LogDebug("Stopping SIP registration user agent for {SIPAccountAOR}.", m_sipAccountAOR);

                if (!m_exit)
                {
                    m_exit = true;
                    m_waitForRegistrationMRE.Set();

                    if (m_isRegistered && sendZeroExpiryRegister)
                    {
                        m_attempts = 0;
                        m_expiry = 0;
                        ThreadPool.QueueUserWorkItem(delegate { SendInitialRegister(); });
                    }
                }

                if (m_registrationTimer != null)
                {
                    m_registrationTimer.Dispose();
                    m_registrationTimer = null;
                }
            }
            catch (Exception excp)
            {
                logger.LogError(excp, "Exception SIPRegistrationUserAgent Stop.");
            }
        }

        private void SendInitialRegister()
        {
            try
            {
                if (m_attempts >= m_maxRegisterAttempts)
                {
                    logger.LogWarning("Registration to {SIPAccountAOR} reached the maximum number of allowed attempts without a failure condition.", m_sipAccountAOR);
                    m_isRegistered = false;
                    RegistrationTemporaryFailure?.Invoke(m_sipAccountAOR, null, "Registration reached the maximum number of allowed attempts.");
                    m_waitForRegistrationMRE.Set();
                }
                else
                {
                    m_attempts++;

                    SIPEndPoint registrarSIPEndPoint = m_outboundProxy;
                    if (registrarSIPEndPoint == null)
                    {
                        SIPURI uri = SIPURI.ParseSIPURIRelaxed(m_registrarHost);
                        var lookupResult = m_sipTransport.ResolveSIPUriAsync(uri).Result;
                        if (lookupResult == null || lookupResult == SIPEndPoint.Empty)
                        {
                            logger.LogWarning("Could not resolve {RegistrarHost} when sending initial registration request.", m_registrarHost);
                        }
                        else
                        {
                            registrarSIPEndPoint = lookupResult;
                        }
                    }

                    if (registrarSIPEndPoint == null)
                    {
                        logger.LogWarning("SIPRegistrationAgent could not resolve {RegistrarHost}.", m_registrarHost);
                        RegistrationFailed?.Invoke(m_sipAccountAOR, null, $"Could not resolve {m_registrarHost}.");
                    }
                    else
                    {
                        logger.LogDebug("Initiating registration to {RegistrarHost} at {RegistrarSIPEndPoint} for {SIPAccountAOR}.", m_registrarHost, registrarSIPEndPoint, m_sipAccountAOR);
                        SIPRequest regRequest = GetRegistrationRequest();

                        SIPNonInviteTransaction regTransaction = new SIPNonInviteTransaction(m_sipTransport, regRequest, registrarSIPEndPoint);
                        regTransaction.NonInviteTransactionFinalResponseReceived += (lep, rep, tn, rsp) => { ServerResponseReceived(lep, rep, tn, rsp); return Task.FromResult(SocketError.Success); };
                        regTransaction.NonInviteTransactionFailed += RegistrationTransactionFailed;

                        regTransaction.SendRequest();
                    }
                }
            }
            catch (Exception excp)
            {
                logger.LogError(excp, "Exception SendInitialRegister to {RegistrarHost}.", m_registrarHost);
                RegistrationFailed?.Invoke(m_sipAccountAOR, null, "Exception SendInitialRegister to " + m_registrarHost + ". " + excp.Message);
            }
        }

        private void RegistrationTransactionFailed(SIPTransaction sipTransaction, SocketError failureReason)
        {
            m_isRegistered = false;
            RegistrationTemporaryFailure?.Invoke(m_sipAccountAOR, null, $"Registration transaction to {m_registrarHost} for {m_sipAccountAOR} failed with {failureReason}.");
            m_waitForRegistrationMRE.Set();
        }

        /// <summary>
        /// The event handler for responses to the initial register request.
        /// </summary>
        private void ServerResponseReceived(SIPEndPoint localSIPEndPoint, SIPEndPoint remoteEndPoint, SIPTransaction sipTransaction, SIPResponse sipResponse)
        {
            try
            {
                logger.LogDebug("Server response {SipResponseStatus} received for {SipAccountAOR}.", sipResponse.Status, m_sipAccountAOR);

                if (sipResponse.Status == SIPResponseStatusCodesEnum.ProxyAuthenticationRequired || sipResponse.Status == SIPResponseStatusCodesEnum.Unauthorised)
                {
                    if (sipResponse.Header.HasAuthenticationHeader)
                    {
                        if (m_attempts >= m_maxRegisterAttempts)
                        {
                            logger.LogDebug("Registration to {SIPAccountAOR} reached the maximum number of allowed attempts without a failure condition.", m_sipAccountAOR);
                            m_isRegistered = false;
                            RegistrationTemporaryFailure?.Invoke(m_sipAccountAOR, sipResponse, "Registration reached the maximum number of allowed attempts.");
                            m_waitForRegistrationMRE.Set();
                        }
                        else
                        {
                            m_attempts++;

                            string username = (m_authUsername != null) ? m_authUsername : m_sipAccountAOR.User;
                            var authenticatedRequest = sipTransaction.TransactionRequest.DuplicateAndAuthenticate(
                                sipResponse.Header.AuthenticationHeaders, username, m_password);

                            SIPEndPoint registrarSIPEndPoint = m_outboundProxy;
                            if (registrarSIPEndPoint == null)
                            {
                                SIPURI uri = SIPURI.ParseSIPURIRelaxed(m_registrarHost);
                                var lookupResult = m_sipTransport.ResolveSIPUriAsync(uri).Result;
                                if (lookupResult == null)
                                {
                                    logger.LogWarning("Could not resolve {RegistrarHost}.", m_registrarHost);
                                }
                                else
                                {
                                    registrarSIPEndPoint = lookupResult;
                                }
                            }
                            if (registrarSIPEndPoint == null)
                            {
                                logger.LogWarning("SIPRegistrationAgent could not resolve {RegistrarHost}.", m_registrarHost);

                                RegistrationFailed?.Invoke(m_sipAccountAOR, sipResponse, "Could not resolve " + m_registrarHost + ".");
                            }
                            else
                            {
                                SIPNonInviteTransaction regAuthTransaction = new SIPNonInviteTransaction(m_sipTransport, authenticatedRequest, registrarSIPEndPoint);
                                regAuthTransaction.NonInviteTransactionFinalResponseReceived += (lep, rep, tn, rsp) =>
                                {
                                    AuthResponseReceived(lep, rep, tn, rsp);
                                    return Task.FromResult(SocketError.Success);
                                };
                                regAuthTransaction.NonInviteTransactionFailed += RegistrationTransactionFailed;
                                regAuthTransaction.SendRequest();

                                // make sure CSeq does not decrease
                                m_cseq = Math.Max(m_cseq, authenticatedRequest.Header.CSeq);
                            }
                        }
                    }
                    else
                    {
                        logger.LogWarning("Registration failed with {Status} but no authentication header was supplied for {SIPAccountAOR}.", sipResponse.Status, m_sipAccountAOR);
                        m_isRegistered = false;
                        RegistrationTemporaryFailure?.Invoke(m_sipAccountAOR, sipResponse, $"Registration failed with {sipResponse.Status} but no authentication header was supplied.");
                        m_waitForRegistrationMRE.Set();
                    }
                }
                else
                {
                    if (sipResponse.Status == SIPResponseStatusCodesEnum.Ok)
                    {
                        if (m_expiry > 0)
                        {
                            m_isRegistered = true;
                            m_expiry = GetUpdatedExpiry(sipTransaction.TransactionRequest, sipResponse);
                            RegistrationSuccessful?.Invoke(m_sipAccountAOR, sipResponse);
                        }
                        else
                        {
                            m_isRegistered = false;
                            RegistrationRemoved?.Invoke(m_sipAccountAOR, sipResponse);
                        }

                        m_waitForRegistrationMRE.Set();
                    }
                    else if (sipResponse.Status == SIPResponseStatusCodesEnum.Forbidden || sipResponse.Status == SIPResponseStatusCodesEnum.NotFound)
                    {
                        // SIP account does not appear to exist.
                        m_exit = m_exitOnUnequivocalFailure;

                        logger.LogWarning("Registration unequivocal failure with {Status} for {SIPAccountAOR}. No further registration attempts will be made: {Exit}.", sipResponse.Status, m_sipAccountAOR, m_exit);
                        string reasonPhrase = (sipResponse.ReasonPhrase.IsNullOrBlank()) ? sipResponse.Status.ToString() : sipResponse.ReasonPhrase;
                        RegistrationFailed?.Invoke(m_sipAccountAOR, sipResponse, "Registration failed with " + (int)sipResponse.Status + " " + reasonPhrase + ".");

                        m_waitForRegistrationMRE.Set();
                    }
                    else if (sipResponse.Status == SIPResponseStatusCodesEnum.IntervalTooBrief && m_expiry != 0)
                    {
                        m_expiry = GetUpdatedExpiryForIntervalTooBrief(sipResponse);
                        logger.LogWarning("Registration for {SIPAccountAOR} had a too short expiry, updated to {Expiry} and trying again.", m_sipAccountAOR, m_expiry);
                        SendInitialRegister();
                    }
                    else
                    {
                        logger.LogWarning("Registration failed with {Status} for {SIPAccountAOR}.", sipResponse.Status, m_sipAccountAOR);
                        m_isRegistered = false;
                        RegistrationTemporaryFailure?.Invoke(m_sipAccountAOR, sipResponse, $"Registration failed with {sipResponse.Status}.");
                        m_waitForRegistrationMRE.Set();
                    }
                }
            }
            catch (Exception excp)
            {
                logger.LogError(excp, "Exception SIPRegistrationUserAgent ServerResponseReceived ({RemoteEndPoint}). {ErrorMessage}", remoteEndPoint, excp.Message);
            }
        }

        /// <summary>
        /// The event handler for responses to the authenticated register request.
        /// </summary>
        private void AuthResponseReceived(SIPEndPoint localSIPEndPoint, SIPEndPoint remoteEndPoint, SIPTransaction sipTransaction, SIPResponse sipResponse)
        {
            try
            {
                logger.LogDebug("Server auth response {Status} received for {SIPAccountAOR}.", sipResponse.Status, m_sipAccountAOR);

                if (sipResponse.Status == SIPResponseStatusCodesEnum.Ok)
                {
                    if (m_expiry > 0)
                    {
                        m_isRegistered = true;
                        m_expiry = GetUpdatedExpiry(sipTransaction.TransactionRequest, sipResponse);
                        RegistrationSuccessful?.Invoke(m_sipAccountAOR, sipResponse);
                    }
                    else
                    {
                        m_isRegistered = false;
                        RegistrationRemoved?.Invoke(m_sipAccountAOR, sipResponse);
                    }

                    m_waitForRegistrationMRE.Set();
                }
                else if (sipResponse.Status == SIPResponseStatusCodesEnum.IntervalTooBrief && m_expiry != 0)
                {
                    m_expiry = GetUpdatedExpiryForIntervalTooBrief(sipResponse);
                    logger.LogDebug("Registration for {SIPAccountAOR} had a too short expiry, updated to {Expiry} and trying again.", m_sipAccountAOR, m_expiry);
                    SendInitialRegister();
                }
                else if (sipResponse.Status == SIPResponseStatusCodesEnum.Forbidden || sipResponse.Status == SIPResponseStatusCodesEnum.NotFound || sipResponse.Status == SIPResponseStatusCodesEnum.PaymentRequired)
                {
                    // SIP account does not appear to exist.
                    m_exit = m_exitOnUnequivocalFailure;

                    logger.LogWarning("Registration unequivocal failure with {Status} for {SipAccountAOR}{Action}.", sipResponse.Status, m_sipAccountAOR, (m_exit ? " ,no further registration attempts will be made" : ""));
                    string reasonPhrase = (sipResponse.ReasonPhrase.IsNullOrBlank()) ? sipResponse.Status.ToString() : sipResponse.ReasonPhrase;
                    RegistrationFailed?.Invoke(m_sipAccountAOR, sipResponse, "Registration failed with " + (int)sipResponse.Status + " " + reasonPhrase + ".");

                    m_waitForRegistrationMRE.Set();
                }
                else if (sipResponse.Status == SIPResponseStatusCodesEnum.ProxyAuthenticationRequired || sipResponse.Status == SIPResponseStatusCodesEnum.Unauthorised)
                {
                    // SIP account credentials failed.
                    m_exit = m_exitOnUnequivocalFailure;

                    logger.LogWarning("Registration unequivocal failure with {Status} for {SipAccountAOR}{Action}.", sipResponse.Status, m_sipAccountAOR, (m_exit ? " ,no further registration attempts will be made" : ""));
                    string reasonPhrase = (sipResponse.ReasonPhrase.IsNullOrBlank()) ? sipResponse.Status.ToString() : sipResponse.ReasonPhrase;
                    RegistrationFailed?.Invoke(m_sipAccountAOR, sipResponse, "Registration failed with " + (int)sipResponse.Status + " " + reasonPhrase + ".");

                    m_waitForRegistrationMRE.Set();
                }
                else
                {
                    logger.LogWarning("Registration failed with {Status} for {SipAccountAOR}.", sipResponse.Status, m_sipAccountAOR);
                    m_isRegistered = false;
                    RegistrationTemporaryFailure?.Invoke(m_sipAccountAOR, sipResponse, "Registration failed with " + sipResponse.Status + ".");
                    m_waitForRegistrationMRE.Set();
                }
            }
            catch (Exception excp)
            {
                logger.LogError(excp, "Exception SIPRegistrationUserAgent AuthResponseReceived. {ErrorMessage}", excp.Message);
            }
        }

        private long GetUpdatedExpiryForIntervalTooBrief(SIPResponse sipResponse)
        {
            long newExpiry = (sipResponse.Header.MinExpires > UInt32.MaxValue) ? UInt32.MaxValue : sipResponse.Header.MinExpires;

            if (newExpiry != 0 && newExpiry > m_expiry)
            {
                if (newExpiry > MAX_EXPIRY)
                {
                    return MAX_EXPIRY;
                }
                else
                {
                    return newExpiry;
                }
            }
            else if (m_expiry < MAX_EXPIRY)
            {
                return m_expiry * 2;
            }

            return m_expiry;
        }

        /// <summary>
        /// Find the contact in the list that matches the one being maintained by this agent in order to determine the expiry value or as defined in the response expires header in that order.
        /// </summary>
        /// <param name="sipRequest"></param>
        /// <param name="sipResponse"></param>
        /// <returns>expiry value returned from the server, otherwise -1 if no value is provided by the server.</returns>
        private long GetServerExpiresFromResponse(SIPRequest sipRequest, SIPResponse sipResponse)
        {
            return sipResponse.Header.Contact?.FirstOrDefault(x =>
                    sipRequest.Header.Contact.Any(y => x.ContactURI.ToParameterlessString() == y.ContactURI.ToParameterlessString()))?.Expires ??
                    ((sipResponse.Header.Expires > uint.MaxValue) ? uint.MaxValue : sipResponse.Header.Expires);
        }

        private long GetUpdatedExpiry(SIPRequest sipRequest, SIPResponse sipResponse)
        {
            long serverExpires = GetServerExpiresFromResponse(sipRequest, sipResponse);

            var result = (serverExpires != -1) ? serverExpires : m_expiry;

            return Math.Max(Math.Min(result, MAX_EXPIRY), REGISTER_MINIMUM_EXPIRY);
        }

        private SIPRequest GetRegistrationRequest()
        {
            SIPURI registerURI = m_sipAccountAOR.CopyOf();
            registerURI.User = null;

            SIPRequest registerRequest = SIPRequest.GetRequest(
                SIPMethodsEnum.REGISTER,
                registerURI,
                new SIPToHeader(this.UserDisplayName, m_sipAccountAOR, null),
                new SIPFromHeader(this.UserDisplayName, m_sipAccountAOR, CallProperties.CreateNewTag()));

            registerRequest.Header.Contact = new List<SIPContactHeader> { new SIPContactHeader(this.UserDisplayName, m_contactURI) };
            registerRequest.Header.CSeq = ++m_cseq;
            registerRequest.Header.CallId = m_callID;
            registerRequest.Header.UserAgent = (!UserAgent.IsNullOrBlank()) ? UserAgent : SIPConstants.SipUserAgentVersionString;
            registerRequest.Header.Expires = m_expiry;
            if (m_overridenAllowHeaderValue != null)
            {
                registerRequest.Header.Allow = m_overridenAllowHeaderValue;
            }

            if (m_customHeaders != null && m_customHeaders.Length > 0)
            {
                foreach (var header in m_customHeaders)
                {
                    registerRequest.Header.UnknownHeaders.Add(header);
                }
            }

            if (AdjustRegister == null)
            {
                return registerRequest;
            }

            return AdjustRegister(registerRequest);
        }
    }
}
