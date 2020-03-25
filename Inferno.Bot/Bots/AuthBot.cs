// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Dialogs;
using Microsoft.Bot.Schema;
using Microsoft.Extensions.Logging;

namespace Inferno.Bot.Bots
{
    public class AuthBot<T> : DialogBot<T> where T : Dialog
    {
        public AuthBot(ConversationState conversationState, UserState userState, T dialog, ConcurrentDictionary<string, ConversationReference> conversationReferences, ILogger<DialogBot<T>> logger)
            : base(conversationState, userState, dialog, conversationReferences, logger)
        {
        }

        protected override async Task OnMembersAddedAsync(IList<ChannelAccount> membersAdded, ITurnContext<IConversationUpdateActivity> turnContext, CancellationToken cancellationToken)
        {
            foreach (var member in turnContext.Activity.MembersAdded)
            {
                if (member.Id != turnContext.Activity.Recipient.Id)
                {
                    await SendGreeting(turnContext, cancellationToken);
                    var initialPrompt = MessageFactory.Text("What would you like to do?");

                    initialPrompt.SuggestedActions = new SuggestedActions()
                    {
                        Actions = new List<CardAction>()
                        {
                            new CardAction() { Title = "Smoke", Type = ActionTypes.ImBack, Value = "Smoke" },
                            new CardAction() { Title = "Hold", Type = ActionTypes.ImBack, Value = "Hold" },
                            new CardAction() { Title = "Status", Type = ActionTypes.ImBack, Value = "Status" },
                            new CardAction() { Title = "Shutdown", Type = ActionTypes.ImBack, Value = "Shutdown" },
                        },
                    };
                    await turnContext.SendActivityAsync(initialPrompt, cancellationToken);
                }
            }
        }

        private async Task SendGreeting(ITurnContext<IConversationUpdateActivity> turnContext, CancellationToken cancellationToken)
        {
            var heroCard = new HeroCard
            {
                Title = "Inferno",
                Subtitle = "Intelligent Smoker",
                Text = "Let's get smoking!",
                Images = new List<CardImage> { new CardImage("https://infernobot.blob.core.windows.net/images/grill.jpg") },
            };

            // So we need to create a list of attachments for the reply activity.
            var attachments = new List<Attachment>();
            // Reply to the activity we received with an activity.
            var greeting = MessageFactory.Attachment(attachments);
            greeting.Attachments.Add(heroCard.ToAttachment());
            await turnContext.SendActivityAsync(greeting, cancellationToken);
        }

        protected override async Task OnTokenResponseEventAsync(ITurnContext<IEventActivity> turnContext, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Running dialog with Token Response Event Activity.");

            // Run the Dialog with the new Token Response Event Activity.
            await _dialog.RunAsync(turnContext, _conversationState.CreateProperty<DialogState>(nameof(DialogState)), cancellationToken);
        }
    }
}
