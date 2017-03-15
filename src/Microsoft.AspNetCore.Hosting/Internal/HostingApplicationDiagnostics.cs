// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Microsoft.AspNetCore.Hosting.Internal
{
    internal class HostingApplicationDiagnostics
    {
        private static readonly double TimestampToTicks = TimeSpan.TicksPerSecond / (double)Stopwatch.Frequency;

        private const string ActivityName = "Microsoft.AspNetCore.Hosting.HttpRequestIn";
        private const string ActivityStartKey = "Microsoft.AspNetCore.Hosting.HttpRequestIn.Start";

        private const string DiagnosticsBeginRequestKey = "Microsoft.AspNetCore.Hosting.BeginRequest";
        private const string DiagnosticsEndRequestKey = "Microsoft.AspNetCore.Hosting.EndRequest";
        private const string DiagnosticsUnhandledExceptionKey = "Microsoft.AspNetCore.Hosting.UnhandledException";

        private const string RequestIdHeaderName = "Request-Id";
        private const string CorrelationContextHeaderName = "Correlation-Context";

        private readonly DiagnosticListener _diagnosticListener;
        private readonly ILogger _logger;

        public HostingApplicationDiagnostics(ILogger logger, DiagnosticListener diagnosticListener)
        {
            _logger = logger;
            _diagnosticListener = diagnosticListener;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void BeginRequest(HttpContext httpContext, ref HostingApplication.Context context)
        {
            // These enabled checks are virtual dispatch and used twice and so cache to locals
            var diagnosticsEnabled = _diagnosticListener.IsEnabled();
            var legacyDiagnosticsEnabled = diagnosticsEnabled && _diagnosticListener.IsEnabled(DiagnosticsBeginRequestKey);
            var loggingEnabled = _logger.IsEnabled(LogLevel.Information);
            var eventLogEnabled = HostingEventSource.Log.IsEnabled();

            if (eventLogEnabled)
            {
                context.EventLogEnabled = true;
                // To keep the hot path short we defer logging in this function to non-inlines
                RecordRequestStartEventLog(httpContext);
            }

            // Only make call GetTimestamp if its value will be used, i.e. of the listenters is enabled
            var startTimestamp = (legacyDiagnosticsEnabled || loggingEnabled) ? Stopwatch.GetTimestamp() : 0;

            // Scope may be relevant for a different level of logging, so we always create it
            // see: https://github.com/aspnet/Hosting/pull/944
            context.Scope = HostingLoggerExtensions.RequestScope(_logger, httpContext);

            if (diagnosticsEnabled && _diagnosticListener.IsEnabled(ActivityName, httpContext))
            {
                context.Activity = StartActivity(httpContext);
            }
            if (loggingEnabled)
            {
                // Non-inline
                LogRequestStarting(httpContext);
            }
            if (legacyDiagnosticsEnabled)
            {
                // Non-inline
                RecordBeginRequestDiagnostics(httpContext, startTimestamp);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void RequestEnd(HttpContext httpContext, Exception exception, HostingApplication.Context context)
        {
            // Local cache items resolved multiple items, in order of use so they are primed in cpu pipeline when used
            var startTimestamp = context.StartTimestamp;
            var diagnosticsEnabled = _diagnosticListener.IsEnabled();

            // If startTimestamp is 0, don't call GetTimestamp, likely don't need the value
            var currentTimestamp = (startTimestamp != 0) ? Stopwatch.GetTimestamp() : 0;

            // To keep the hot path short we defer logging to non-inlines
            if (exception == null)
            {
                // No exception was thrown, request was sucessful
                if (diagnosticsEnabled && _diagnosticListener.IsEnabled(DiagnosticsEndRequestKey))
                {
                    // Diagnostics is enabled for EndRequest, but it may not be for BeginRequest
                    // so call GetTimestamp if currentTimestamp is zero (from above)
                    RecordEndRequestDiagnostics(
                        httpContext,
                        (currentTimestamp != 0) ? currentTimestamp : Stopwatch.GetTimestamp());
                }
            }
            else
            {
                // Exception was thrown from request
                if (diagnosticsEnabled && _diagnosticListener.IsEnabled(DiagnosticsUnhandledExceptionKey))
                {
                    // Diagnostics is enabled for UnhandledException, but it may not be for BeginRequest
                    // so call GetTimestamp if currentTimestamp is zero (from above)
                    RecordUnhandledExceptionDiagnostics(
                        httpContext,
                        (currentTimestamp != 0) ? currentTimestamp : Stopwatch.GetTimestamp(),
                        exception);
                }

                if (context.EventLogEnabled)
                {
                    // Non-inline
                    HostingEventSource.Log.UnhandledException();
                }
            }

            // If startTimestamp was 0, then Information logging wasn't enabled at for this request (and calcuated time will be wildly wrong)
            // Is used as proxy to reduce calls to virtual: _logger.IsEnabled(LogLevel.Information)
            if (startTimestamp != 0)
            {
                // Non-inline
                LogRequestFinished(httpContext, startTimestamp, currentTimestamp);
            }

            var activity = context.Activity;
            // Always stop activity if it was started
            if (activity != null)
            {
                StopActivity(httpContext, activity);
            }

            // Logging Scope is finshed with
            context.Scope?.Dispose();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ContextDisposed(HostingApplication.Context context)
        {
            if (HostingEventSource.Log.IsEnabled())
            {
                // Non-inline
                HostingEventSource.Log.RequestStop();
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void LogRequestStarting(HttpContext httpContext)
        {
            // IsEnabled is checked in the caller, so if we are here just log
            _logger.Log(
                logLevel: LogLevel.Information,
                eventId: LoggerEventIds.RequestStarting,
                state: new HostingRequestStartingLog(httpContext),
                exception: null,
                formatter: HostingRequestStartingLog.Callback);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void LogRequestFinished(HttpContext httpContext, long startTimestamp, long currentTimestamp)
        {
            // IsEnabled isn't checked in the caller, startTimestamp > 0 is used as a fast proxy check
            // but that may be because diagnostics are enabled, which also uses startTimestamp, so check here
            if (_logger.IsEnabled(LogLevel.Information))
            {
                var elapsed = new TimeSpan((long)(TimestampToTicks * (currentTimestamp - startTimestamp)));

                _logger.Log(
                    logLevel: LogLevel.Information,
                    eventId: LoggerEventIds.RequestFinished,
                    state: new HostingRequestFinishedLog(httpContext, elapsed),
                    exception: null,
                    formatter: HostingRequestFinishedLog.Callback);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void RecordBeginRequestDiagnostics(HttpContext httpContext, long startTimestamp)
        {
            _diagnosticListener.Write(
                DiagnosticsBeginRequestKey,
                new
                {
                    httpContext = httpContext,
                    timestamp = startTimestamp
                });
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void RecordEndRequestDiagnostics(HttpContext httpContext, long currentTimestamp)
        {
            _diagnosticListener.Write(
                DiagnosticsEndRequestKey,
                new
                {
                    httpContext = httpContext,
                    timestamp = currentTimestamp
                });
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void RecordUnhandledExceptionDiagnostics(HttpContext httpContext, long currentTimestamp, Exception exception)
        {
            _diagnosticListener.Write(
                DiagnosticsUnhandledExceptionKey,
                new
                {
                    httpContext = httpContext,
                    timestamp = currentTimestamp,
                    exception = exception
                });
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RecordRequestStartEventLog(HttpContext httpContext)
        {
            HostingEventSource.Log.RequestStart(httpContext.Request.Method, httpContext.Request.Path);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private Activity StartActivity(HttpContext httpContext)
        {
            var activity = new Activity(ActivityName);
            if (httpContext.Request.Headers.TryGetValue(RequestIdHeaderName, out var requestId))
            {
                try
                {
                    activity.SetParentId(requestId);
                }
                catch (ArgumentException ex)
                {
                    _logger.LogWarning(LoggerEventIds.InvalidRequestIdHeader, ex, "Request ID received in '{RequestIdHeaderName}' header is invalid '{RequestId}'", RequestIdHeaderName, requestId);
                }

                // We expect baggage to be empty by default
                // Only very advanced users will be using it in near future, we encouradge them to keep baggage small (few items)
                string[] baggage = httpContext.Request.Headers.GetCommaSeparatedValues(CorrelationContextHeaderName);
                if (baggage != StringValues.Empty)
                {
                    foreach (var item in baggage)
                    {
                        if (NameValueHeaderValue.TryParse(item, out var baggageItem))
                        {
                            try
                            {
                                activity.AddBaggage(baggageItem.Name, baggageItem.Value);
                            }
                            catch (ArgumentException ex)
                            {
                                _logger.LogWarning(LoggerEventIds.InvalidRequestIdHeader, ex, "Baggage item received in '{BaggageHeaderName}' header is invalid '{ItemName}' with value '{ItemValue}'", CorrelationContextHeaderName, baggageItem.Name, baggageItem.Value);
                            }
                        }
                    }
                }
            }

            if (_diagnosticListener.IsEnabled(ActivityStartKey, activity, httpContext))
            {
                _diagnosticListener.StartActivity(activity, new { HttpContext = httpContext });
            }
            else
            {
                activity.Start();
            }

            return activity;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void StopActivity(HttpContext httpContext, Activity activity)
        {
            _diagnosticListener.StopActivity(activity, new { HttpContext = httpContext });
        }
    }
}