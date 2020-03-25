// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Inferno.Bot.Services;
using Inferno.Common.Models;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Dialogs;
using Microsoft.Bot.Builder.Dialogs.Choices;
using Microsoft.Bot.Schema;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Linq;
using Inferno.Bot.Models;

namespace Inferno.Bot.Dialogs
{
    public class MainDialog : LogoutDialog
    {
        protected readonly ILogger _logger;
        protected readonly SmokerRelayClient _smoker;
        private static readonly MemoryStorage _storage = new MemoryStorage();
        private readonly string _authLoopKey = "Dialog.Main";
        private readonly string _smokerLoopKey = "Dialog.Smoker";
        private readonly string _oAuthPromptKey = "Prompt.OAuth";
        private readonly string _modePromptKey = "Prompt.Mode";
        private readonly string _setpointKey = "Prompt.Setpoint";
        private readonly string _shutdownKey = "Prompt.Shutdown";
        private readonly string _errText = "I'm sorry, I wasn't able to do that.";
        private readonly string _prePromptUtteranceKey = "PrePromptUtterance"; 

        public MainDialog(IConfiguration configuration, ILogger<MainDialog> logger, SmokerRelayClient smoker)
            : base(nameof(MainDialog), configuration["ConnectionName"])
        {
            _logger = logger;
            _smoker = smoker;

            AddDialog(new OAuthPrompt(
                _oAuthPromptKey,
                new OAuthPromptSettings
                {
                    ConnectionName = ConnectionName,
                    Text = "Please sign in to continue.",
                    Title = "Inferno Sign In", 
                    Timeout = 300000, // User has 5 minutes to login (1000 * 60 * 5)
                }));

            AddDialog(new TextPrompt(_modePromptKey));

            AddDialog(new NumberPrompt<int>(_setpointKey));

            AddDialog(new ConfirmPrompt(_shutdownKey));

            AddDialog(new WaterfallDialog(_authLoopKey, new WaterfallStep[]
            {
                PromptLogin,
                ProcessLogin
            }));

            AddDialog(new WaterfallDialog(_smokerLoopKey, new WaterfallStep[]
            {
                PromptCommand,
                ExecuteCommand,
                FinalizeCommand
            }));

            // The initial child Dialog to run.
            InitialDialogId = _authLoopKey;
        }

        private async Task<DialogTurnResult> PromptLogin(WaterfallStepContext stepContext, CancellationToken cancellationToken)
        {
            await StorePrePromptUtterance(stepContext, cancellationToken);
            return await stepContext.BeginDialogAsync(_oAuthPromptKey, null, cancellationToken);
        }

        private async Task StorePrePromptUtterance(WaterfallStepContext stepContext, CancellationToken cancellationToken)
        {
            var changes = new Dictionary<string, object>();
            changes.Add(_prePromptUtteranceKey, new Utterance() { Text = stepContext.Context.Activity.Text.ToLower() });
            await _storage.WriteAsync(changes, cancellationToken);
        }

        private async Task<DialogTurnResult> ProcessLogin(WaterfallStepContext stepContext, CancellationToken cancellationToken)
        {
            // Get the token from the previous step. Note that we could also have gotten the
            // token directly from the prompt itself. There is an example of this in the next method.
            var tokenResponse = (TokenResponse)stepContext.Result;
            if (tokenResponse != null)
            {
                return await stepContext.BeginDialogAsync(_smokerLoopKey, null, cancellationToken);
            }

            await stepContext.Context.SendActivityAsync(MessageFactory.Text("Login was not successful. Please try again."), cancellationToken);
            return await stepContext.EndDialogAsync(cancellationToken: cancellationToken);
        }


        private async Task SendStatus(WaterfallStepContext stepContext, CancellationToken cancellationToken)
        {

            SmokerStatus status = await _smoker.GetStatus();

            string modeText = (status.Mode == SmokerMode.Hold.ToString()) ?
                                $"{status.Mode} {status.SetPoint}°F" :
                                status.Mode;

            string probeText = (status.Temps.ProbeTemp == -1) ?
                                   "Unplugged" : $"{status.Temps.ProbeTemp}°F";

            var statusText = $"**Mode:** {modeText}\n";
            statusText += $"**Grill:** {status.Temps.GrillTemp}°F\n";
            statusText += $"**Probe:** {probeText}\n";


            await stepContext.Context.SendActivityAsync(MessageFactory.Text(statusText), cancellationToken);
        }

        private async Task<DialogTurnResult> PromptCommand(WaterfallStepContext stepContext, CancellationToken cancellationToken)
        {
            var prePromptUtterance = (await _storage.ReadAsync<Utterance>(new string[] { _prePromptUtteranceKey }))?.FirstOrDefault().Value;
            if (prePromptUtterance != null)
            {
                return await ExecuteCommand(stepContext, cancellationToken);
            }
            else
            {
                var modePrompt = MessageFactory.Text("What would you like to do?");

                modePrompt.SuggestedActions = new SuggestedActions()
                {
                    Actions = new List<CardAction>()
                    {
                        new CardAction() { Title = "Smoke", Type = ActionTypes.ImBack, Value = "Smoke" },
                        new CardAction() { Title = "Hold", Type = ActionTypes.ImBack, Value = "Hold" },
                        new CardAction() { Title = "Status", Type = ActionTypes.ImBack, Value = "Status" },
                        new CardAction() { Title = "Shutdown", Type = ActionTypes.ImBack, Value = "Shutdown" },
                    },
                };
                return await stepContext.PromptAsync(_modePromptKey, new PromptOptions { Prompt = modePrompt }, cancellationToken);

            }
        }

        private async Task<DialogTurnResult> ExecuteCommand(WaterfallStepContext stepContext, CancellationToken cancellationToken)
        {
            string resultText;
            var prePromptUtterance = (await _storage.ReadAsync<Utterance>(new string[] { _prePromptUtteranceKey }))?.FirstOrDefault().Value;
            if (prePromptUtterance != null)
            {
                resultText = prePromptUtterance.Text;
                await _storage.DeleteAsync(new string[] { _prePromptUtteranceKey }, cancellationToken);
            }
            else
            {
                resultText = stepContext.Result.ToString().ToLower();
            }
            
            switch(resultText)
            {
                case "status":
                    await SendStatus(stepContext, cancellationToken);
                    return await stepContext.ReplaceDialogAsync(_smokerLoopKey, null, cancellationToken);

                case "hold":
                    var promptOptions = new PromptOptions
                    {
                        Prompt = MessageFactory.Text("What temperature?"),
                        RetryPrompt = MessageFactory.Text("Please enter a numerical value."),
                    };
                    return await stepContext.PromptAsync(_setpointKey, promptOptions, cancellationToken);
                
                case "shutdown":
                    return await stepContext.PromptAsync(_shutdownKey, new PromptOptions { Prompt = MessageFactory.Text("Are you sure you want to shut down? The smoker will be in shutdown mode for 10 minutes and unable to accept any other commands.") }, cancellationToken);

                case "smoke":
                    await SetSmoke(stepContext);
                    return await stepContext.ReplaceDialogAsync(_smokerLoopKey, null, cancellationToken);
            }

            int setpoint;
            if (resultText.StartsWith("hold") && 
                    int.TryParse(resultText.Substring(4), out setpoint))
            {
                await SetHold(setpoint, stepContext, cancellationToken);
                return await stepContext.ReplaceDialogAsync(_smokerLoopKey, null, cancellationToken);
            }

            await stepContext.Context.SendActivityAsync(MessageFactory.Text("I'm sorry, I don't understand that."), cancellationToken);
            return await stepContext.ReplaceDialogAsync(_smokerLoopKey, null, cancellationToken);
        }

        private async Task SetSmoke(WaterfallStepContext stepContext)
        {
            bool success = await _smoker.SetMode(SmokerMode.Smoke);
            if (!success)
            {
                await stepContext.Context.SendActivityAsync(MessageFactory.Text(_errText));
            }
            else
            {
                await stepContext.Context.SendActivityAsync(MessageFactory.Text("Setting Smoke."));
            }
        }

        private async Task<DialogTurnResult> FinalizeCommand(WaterfallStepContext stepContext, CancellationToken cancellationToken)
        {
            if (stepContext.Result.GetType() == typeof(bool))
            {
                if ((bool)stepContext.Result)
                {
                    await SetShutdown(stepContext, cancellationToken);
                }
            }
            else if(stepContext.Result.GetType() == typeof(int))
            {
                var setpoint = (int)stepContext.Result;
                await SetHold(setpoint, stepContext, cancellationToken);
            }

            return await stepContext.ReplaceDialogAsync(_smokerLoopKey, null, cancellationToken);
        }

        private async Task SetShutdown(WaterfallStepContext stepContext, CancellationToken cancellationToken)
        {
            bool success = await _smoker.SetMode(SmokerMode.Shutdown);
            if (success)
            {
                await stepContext.Context.SendActivityAsync(MessageFactory.Text($"Shutting down."), cancellationToken);
            }
            else
            {
                await stepContext.Context.SendActivityAsync(MessageFactory.Text(_errText), cancellationToken);
            }
        }

        private async Task SetHold(int setpoint, WaterfallStepContext stepContext, CancellationToken cancellationToken)
        {
            bool success = await _smoker.SetMode(SmokerMode.Hold) &&
                            await _smoker.SetSetpoint(setpoint);
            if (success)
            {
                await stepContext.Context.SendActivityAsync(MessageFactory.Text($"Setting Hold {setpoint}°F"), cancellationToken);
            }
            else
            {
                await stepContext.Context.SendActivityAsync(MessageFactory.Text(_errText));
            }
        }
    }
}
