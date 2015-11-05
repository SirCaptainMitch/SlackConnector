﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Bazam.NoobWebClient;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SlackConnector.BotHelpers;
using SlackConnector.Connections;
using SlackConnector.Connections.Handshaking;
using SlackConnector.Connections.Handshaking.Models;
using SlackConnector.Connections.Sockets;
using SlackConnector.Connections.Sockets.Messages;
using SlackConnector.EventHandlers;
using SlackConnector.Exceptions;
using SlackConnector.Models;
using Group = SlackConnector.Connections.Handshaking.Models.Group;

namespace SlackConnector
{
    public class SlackConnector : ISlackConnector
    {
        private readonly IConnectionFactory _connectionFactory;
        private IWebSocketClient _webSocketClient;

        private const string SLACK_API_SEND_MESSAGE_URL = "https://slack.com/api/chat.postMessage";
        private const string SLACK_API_JOIN_DM_URL = "https://slack.com/api/im.open";

        public string[] Aliases { get; set; } = new string[0];

        public SlackChatHub[] ConnectedDMs
        {
            get { return ConnectedHubs.Values.Where(hub => hub.Type == SlackChatHubType.DM).ToArray(); }
        }

        public SlackChatHub[] ConnectedChannels
        {
            get { return ConnectedHubs.Values.Where(hub => hub.Type == SlackChatHubType.Channel).ToArray(); }
        }

        public SlackChatHub[] ConnectedGroups
        {
            get { return ConnectedHubs.Values.Where(hub => hub.Type == SlackChatHubType.Group).ToArray(); }
        }

        private readonly Dictionary<string, SlackChatHub> _connectedHubs = new Dictionary<string, SlackChatHub>();
        public IReadOnlyDictionary<string, SlackChatHub> ConnectedHubs => _connectedHubs;

        private readonly Dictionary<string, string> _userNameCache = new Dictionary<string, string>();
        public IReadOnlyDictionary<string, string> UserNameCache => _userNameCache;

        public bool IsConnected => ConnectedSince != null;
        public DateTime? ConnectedSince { get; private set; }
        public string SlackKey { get; private set; }
        public string TeamId { get; private set; }
        public string TeamName { get; private set; }
        public string UserId { get; private set; }
        public string UserName { get; private set; }

        public SlackConnector() : this(new ConnectionFactory())
        { }

        internal SlackConnector(IConnectionFactory connectionFactory)
        {
            _connectionFactory = connectionFactory;
        }

        public async Task Connect(string slackKey)
        {
            if (IsConnected)
            {
                throw new AlreadyConnectedException();
            }

            if (string.IsNullOrEmpty(slackKey))
            {
                throw new ArgumentNullException(nameof(slackKey));
            }

            SlackKey = slackKey;

            IHandshakeClient handshakeClient = _connectionFactory.CreateHandshakeClient();
            SlackHandshake handshake = await handshakeClient.FirmShake(slackKey);

            TeamName = handshake.Team.Name;
            TeamId = handshake.Team.Id;
            UserName = handshake.Self.Name;
            UserId = handshake.Self.Id;

            foreach (User user in handshake.Users)
            {
                _userNameCache.Add(user.Id, user.Name);
            }

            foreach (Channel channel in handshake.Channels)
            {
                var newChannel = new SlackChatHub
                {
                    Id = channel.Id,
                    Name = "#" + channel.Name,
                    Type = SlackChatHubType.Channel
                };
                _connectedHubs.Add(channel.Id, newChannel);
            }

            foreach (Group group in handshake.Groups)
            {
                if (group.Members.Any(x => x == UserId))
                {
                    var newGroup = new SlackChatHub
                    {
                        Id = group.Id,
                        Name = "#" + group.Name,
                        Type = SlackChatHubType.Group
                    };
                    _connectedHubs.Add(group.Id, newGroup);
                }
            }

            foreach (Im im in handshake.Ims)
            {
                var newIm = new SlackChatHub
                {
                    Id = im.Id,
                    Name = "@" + (_userNameCache.ContainsKey(im.User) ? _userNameCache[im.User] : im.User),
                    Type = SlackChatHubType.DM
                };
                _connectedHubs.Add(im.Id, newIm);
            }

            _webSocketClient = _connectionFactory.CreateWebSocketClient(handshake.WebSocketUrl);
            await _webSocketClient.Connect();

            ConnectedSince = DateTime.Now;
            RaiseConnectionStatusChanged();

            _webSocketClient.OnMessage += async (sender, message) => await ListenTo(message);
            _webSocketClient.OnClose += (sender, e) =>
            {
                ConnectedSince = null;
                RaiseConnectionStatusChanged();
            };
        }

        private async Task ListenTo(InboundMessage inboundMessage)
        {
            if (inboundMessage?.MessageType != MessageType.Message)
                return;

            var message = new SlackMessage
            {
                User = new SlackUser
                {
                    Id = inboundMessage.User,
                    Name = UserNameCache.ContainsKey(inboundMessage.User) ? UserNameCache[inboundMessage.User] : string.Empty,
                },
                Text = inboundMessage.Text
            };

            await RaiseMessageReceived(message);

            //if (message != null && message["type"].Value<string>() == "message")
            //{
            //    string channelId = message["channel"].Value<string>();
            //    SlackChatHub hub;

            //    if (ConnectedHubs.ContainsKey(channelId))
            //    {
            //        hub = ConnectedHubs[channelId];
            //    }
            //    else
            //    {
            //        hub = SlackChatHub.FromId(channelId);
            //        if (!_connectedHubs.ContainsKey(channelId))
            //        {
            //            _connectedHubs.Add(channelId, hub);
            //        }
            //    }

            //    string messageText = message["text"]?.Value<string>();

            //    // check to see if bot has been mentioned
            //    var slackMessage = new SlackMessage
            //    {
            //        ChatHub = hub,
            //        MentionsBot = BotMentioned(messageText),
            //        RawData = message.ToString(),
            //        // some messages may not have text or a user (like unfurled data from URLs)
            //        Text = messageText,
            //        User = (message["user"] != null ? new SlackUser { Id = message["user"].Value<string>() } : null)
            //    };

            //    var context = new ResponseContext
            //    {
            //        BotUserId = UserId,
            //        BotUserName = UserName,
            //        Message = slackMessage,
            //        TeamId = TeamId,
            //        UserNameCache = new ReadOnlyDictionary<string, string>(_userNameCache)
            //    };

            //    if (slackMessage.User != null && slackMessage.User.Id != UserId && slackMessage.Text != null)
            //    {
            //        await RaiseMessageReceived(context);
            //    }
            //}

         //   await Task.Factory.StartNew(() => { });
        }

        public void Disconnect()
        {
            if (_webSocketClient != null && _webSocketClient.IsAlive)
            {
                _webSocketClient.Close();
            }
        }

        public async Task Say(BotMessage message)
        {
            string chatHubId = null;

            if (message.ChatHub != null)
            {
                chatHubId = message.ChatHub.Id;
            }

            if (!string.IsNullOrEmpty(chatHubId))
            {
                var values = new List<string>
                {
                    "token", this.SlackKey,
                    "channel", chatHubId,
                    "text", message.Text,
                    "as_user", "true"
                };

                if (message.Attachments.Count > 0)
                {
                    values.Add("attachments");
                    values.Add(JsonConvert.SerializeObject(message.Attachments));
                }

                var client = new NoobWebClient();
                await client.GetResponse(SLACK_API_SEND_MESSAGE_URL, RequestMethod.Post, values.ToArray());
            }
            else
            {
                throw new ArgumentException("When calling the Say() method, the message parameter must have its ChatHub property set.");
            }
        }

        public async Task<SlackChatHub> JoinDirectMessageChannel(string user)
        {
            SlackChatHub chatHub = null;

            var values = new[]
            {
                "token", this.SlackKey,
                "user", user
            };

            var client = new NoobWebClient();
            string json = await client.GetResponse(SLACK_API_JOIN_DM_URL, RequestMethod.Post, values);
            JObject jData = JObject.Parse(json);

            if (jData["ok"] != null && jData["ok"].Value<bool>())
            {
                chatHub = new SlackChatHub
                {
                    Id = jData["channel"]["id"].Value<string>(),
                    Type = SlackChatHubType.DM
                };
            }

            return chatHub;
        }

        private bool BotMentioned(string messageText)
        {
            bool mentioned = false;

            // only build the regex if we're connected - if we're not connected we won't know our bot's name or user Id
            if (IsConnected)
            {
                string regex = new BotNameRegexComposer().ComposeFor(UserName, UserId, Aliases);
                mentioned = (messageText != null && Regex.IsMatch(messageText, regex, RegexOptions.IgnoreCase));
            }

            return mentioned;
        }

        public event ConnectionStatusChangedEventHandler OnConnectionStatusChanged;
        private void RaiseConnectionStatusChanged()
        {
            OnConnectionStatusChanged?.Invoke(IsConnected);
        }

        public event MessageReceivedEventHandler OnMessageReceived;
        private async Task RaiseMessageReceived(SlackMessage message)
        {
            if (OnMessageReceived != null)
            {
                await OnMessageReceived(message);
            }
        }
    }
}