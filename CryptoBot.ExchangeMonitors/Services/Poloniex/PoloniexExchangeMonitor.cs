﻿using System;
using System.Collections.Generic;
using System.Linq;
using CryptoBot.Utils.Assertions;
using CryptoBot.Utils.General;
using CryptoBot.Utils.Helpers;
using CryptoBot.Utils.Logging;
using CryptoBot.Utils.ServiceHandler;
using log4net;
using Newtonsoft.Json.Linq;
using WampSharp.Binding;
using WampSharp.Core.Listener;
using WampSharp.V2;
using WampSharp.V2.Client;
using WampSharp.V2.Realm;
using WampSharp.WebSocket4Net;

namespace CryptoBot.ExchangeMonitors.Services.Poloniex
{
    /// <summary>
    /// Exchange monitor service implementation for Poloniex exchange
    /// </summary>
    public sealed class PoloniexExchangeMonitor : IManagedService, IExchangeMonitor
    {
        private static readonly ILog Logger = ApplicationLogging.CreateLogger<PoloniexExchangeMonitor>();
        private const string ServerAddress = "wss://api.poloniex.com";
        private const string TickerTopic = "ticker";

        private readonly IList<Subscription> _subscriptions = new List<Subscription>();
        private readonly IWampChannelFactory _wampChannelFactory;

        private IDisposable _channelSubscription;
        private IWampChannel _channel;

        /// <summary>
        /// Corresponding exchange for Ticker service
        /// </summary>
        public Exchange Exchange => Exchange.Poloniex;

        /// <summary>
        /// Constructor
        /// </summary>
        public PoloniexExchangeMonitor() : this(new WampChannelFactory()) { }

        /// <summary>
        /// Constructor
        /// </summary>
        public PoloniexExchangeMonitor(IWampChannelFactory wampChannelFactory)
        {
            _wampChannelFactory = Preconditions.CheckNotNull(wampChannelFactory);
        }

        /// <inheritdoc />
        public void Start()
        {
            // Open channel to poloniex
            var mJsonBinding = new JTokenJsonBinding();
			Func<IControlledWampConnection<JToken>> connectionFactory = () => new WebSocket4NetTextConnection<JToken>(ServerAddress, mJsonBinding);
            _channel = _wampChannelFactory.CreateChannel("realm1", connectionFactory, mJsonBinding);
            _channel.RealmProxy.Monitor.ConnectionBroken += OnConnectionBroken;
            _channel.RealmProxy.Monitor.ConnectionEstablished += OnConnectionEstablised;
            _channel.RealmProxy.Monitor.ConnectionError += OnConnectionError;
            _channel.Open().Wait(5000);
            
            Logger.Info("Poloniex ticker service has been started");
        }

        /// <inheritdoc />
        public void Stop()
        {
            // Stop subscription and close channel
            _channelSubscription?.Dispose();
            _channel.Close();

            Logger.Info("Poloniex ticker service has been stopped");
        }

        /// <inheritdoc />
        public void Subscribe(SubscriptionCategory kind, CurrencyPair currencyPair, IExchangeMonitorSubscriber subscriber)
        {
            Preconditions.CheckNotNull(kind);
            Preconditions.CheckNotNull(currencyPair);
            Preconditions.CheckNotNull(subscriber);

            // Subscribe subscriber
            lock (_subscriptions)
            {
                if (FindSubscription(kind, currencyPair, subscriber) == null)
                {
                    _subscriptions.Add(new Subscription(currencyPair, kind, subscriber));
                }
            }
        }

        /// <inheritdoc />
        public void Unsubscribe(SubscriptionCategory kind, CurrencyPair currencyPair, IExchangeMonitorSubscriber subscriber)
        {
            Preconditions.CheckNotNull(kind);
            Preconditions.CheckNotNull(currencyPair);
            Preconditions.CheckNotNull(subscriber);

            // Unsubscribe subscriber
            lock (_subscriptions)
            {
                var subscription = FindSubscription(kind, currencyPair, subscriber);
                if (subscription != null)
                {
                    _subscriptions.Remove(subscription);
                }
            }
        }

        private void OnConnectionEstablised(object sender, WampSessionCreatedEventArgs e)
        {
            Logger.Debug("Connection established to Poloniex exchange monitor");

            // Subscribe to ticker topic
            _channelSubscription = _channel.RealmProxy.Services.GetSubject(TickerTopic).Subscribe(x => ProcessTick(x.Arguments));
        }

        private void OnConnectionError(object sender, WampConnectionErrorEventArgs e)
        {
            Logger.Error("Connection error in Poloniex exchange monitor", e.Exception);
        }

        private void OnConnectionBroken(object sender, WampSessionCloseEventArgs e)
        {
            // Allow disconnecton, but re-initialize connection in case of any other reason
            if (e.CloseType == SessionCloseType.Disconnection)
            {
                return;
            }

            // Re-initialize connection
            Stop();
            Start();
        }

        private void ProcessTick(ISerializedValue[] arguments)
        {
            // Determine applicable currency pair
            var currencyPairStr = arguments[0].Deserialize<string>();
            var currencyPair = CurrencyPair.Parse(currencyPairStr);
            if (ReferenceEquals(currencyPair, null))
            {
                Logger.Debug($"Received tick of unknown currency Pair '{currencyPairStr}', skipping tick");
                return;
            }

            // Convert data to TickData
            var tickData = new TickData.Builder(Exchange, currencyPair, DateTimeHelper.ToUnixTime(DateTime.Now), arguments[1].Deserialize<decimal>())
                .LowestAsk(arguments[2].Deserialize<decimal>())
                .HighestBid(arguments[3].Deserialize<decimal>())
                .PercentChange(arguments[4].Deserialize<decimal>())
                .BaseVolume(arguments[5].Deserialize<decimal>())
                .QuoteVolume(arguments[6].Deserialize<decimal>())
                .IsFrozen(arguments[7].Deserialize<byte>() > 0)
                .DayHigh(arguments[8].Deserialize<decimal>())
                .DayLow(arguments[9].Deserialize<decimal>())
                .Build();

            Logger.Debug($"Received new tick: {tickData}");

            // Notify subscribers
            lock (_subscriptions)
            {
                foreach (var subscription in _subscriptions)
                {
                    subscription.Subscriber.OnTick(Exchange, tickData);
                }
            }
        }

        private Subscription FindSubscription(SubscriptionCategory kind, CurrencyPair currencyPair, IExchangeMonitorSubscriber subscriber)
        {
            lock (_subscriptions)
            {
                return _subscriptions.FirstOrDefault(s =>
                    s.Kind == kind && s.CurrencyPair.Equals(currencyPair) && s.Subscriber == subscriber);
            }
        }
    }
}