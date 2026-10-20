using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Radzen;

namespace SimpleInvoiceManagementSoftware.Shared
{
    /// <summary>
    /// Voice assistant panel: listens to the microphone, shows every step (what was heard, what was
    /// understood, spelling fixes, what was saved or what went wrong) and does the work of its screen.
    /// </summary>
    public partial class VoiceAssistant : ComponentBase, IAsyncDisposable
    {
        private const int MaxLogEntries = 60;

        /// <summary>Which screen this panel is on; decides what it is allowed to do.</summary>
        [Parameter]
        public VoiceScreen Screen { get; set; }

        /// <summary>Raised after something was saved, so the page can refresh its list.</summary>
        [Parameter]
        public EventCallback OnChanged { get; set; }

        [Inject] protected IJSRuntime JSRuntime { get; set; }
        [Inject] protected SpeechToTextService SpeechToText { get; set; }
        [Inject] protected LocalAiService LocalAi { get; set; }
        [Inject] protected VoiceCommandExecutor Executor { get; set; }
        [Inject] protected NotificationService NotificationService { get; set; }

        protected enum VoiceState { Off, Starting, Listening, Hearing, Working }

        protected class LogEntry
        {
            public DateTime Time { get; set; } = DateTime.Now;
            public string Kind { get; set; }      // css suffix: you, info, fix, success, warning, error
            public string Icon { get; set; }
            public string Title { get; set; }
            public string Text { get; set; }
            public List<string> Details { get; set; } = new List<string>();
            public string Link { get; set; }
            public string LinkText { get; set; }
        }

        protected VoiceState state = VoiceState.Off;
        protected string status = "Voice is off. Click the microphone and speak naturally.";
        protected readonly List<LogEntry> log = new List<LogEntry>();
        protected ElementReference meter;
        protected ElementReference logBox;

        private DotNetObjectReference<VoiceAssistant> selfRef;
        private readonly SemaphoreSlim phraseLock = new SemaphoreSlim(1, 1);
        private bool scrollPending;

        protected bool IsOn => state != VoiceState.Off;

        protected string Title => Screen switch
        {
            VoiceScreen.Products => "Add products by voice",
            VoiceScreen.Customers => "Add customers by voice",
            _ => "Create invoices by voice"
        };

        protected string[] Examples => VoiceCommandExecutor.ExamplesFor(Screen);

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (scrollPending)
            {
                scrollPending = false;
                await JSRuntime.InvokeVoidAsync("voiceCommands.scrollToBottom", logBox);
            }
        }

        protected async Task MicClick()
        {
            if (state == VoiceState.Starting)
            {
                return;
            }
            if (IsOn)
            {
                await Stop();
                return;
            }

            state = VoiceState.Starting;
            try
            {
                if (!SpeechToText.IsModelReady)
                {
                    SetStatus("Starting… loading the speech model (the first time it downloads a few hundred MB).");
                    await SpeechToText.EnsureModelLoadedAsync();
                }

                SetStatus("Starting… loading the AI model (the first time this can take a minute).");
                await LocalAi.WarmUpAsync(SetStatus);

                selfRef ??= DotNetObjectReference.Create(this);
                await JSRuntime.InvokeVoidAsync("voiceCommands.start", selfRef, meter);
            }
            catch (Exception ex)
            {
                state = VoiceState.Off;
                status = "Voice is off.";
                var tip = ex is JSException
                    ? "Allow microphone access in the browser (the icon in the address bar), then click the microphone again."
                    : ex.Message.Contains("Ollama")
                        ? "Open “Ollama” from the Windows Start menu, wait about a minute, then click the microphone again."
                        : null;
                AddLog("error", "error", "Could not start voice", ex.Message, tip);
                return;
            }

            state = VoiceState.Listening;
            status = "Listening — speak now, then pause for a second.";
            AddLog("info", "mic", "Microphone on", "I'm listening. Speak naturally and pause when you are done.");
        }

        private async Task Stop()
        {
            state = VoiceState.Off;
            status = "Voice is off. Click the microphone to start again.";
            await JSRuntime.InvokeVoidAsync("voiceCommands.stop");
            AddLog("info", "mic_off", "Microphone off", null);
        }

        protected void ClearLog()
        {
            log.Clear();
        }

        /// <summary>Called from voice-commands.js as soon as the user starts speaking.</summary>
        [JSInvokable]
        public void OnSpeechStarted()
        {
            if (state == VoiceState.Listening)
            {
                state = VoiceState.Hearing;
                status = "I'm hearing you… pause for a second when you're done.";
                StateHasChanged();
            }
        }

        /// <summary>Called from voice-commands.js with one spoken phrase as 16 kHz float32 PCM bytes.</summary>
        [JSInvokable]
        public async Task OnVoicePhrase(byte[] audio)
        {
            await phraseLock.WaitAsync();
            var watch = Stopwatch.StartNew();
            try
            {
                if (!IsOn)
                {
                    return;
                }

                state = VoiceState.Working;
                SetStatus("Got it — turning your speech into text…");

                var samples = new float[audio.Length / sizeof(float)];
                Buffer.BlockCopy(audio, 0, samples, 0, samples.Length * sizeof(float));
                var (products, customers) = await Executor.LoadKnownNamesAsync();
                var knownNames = products.Select(p => p.Name).Concat(customers.Select(c => c.Name)).ToList();

                // Whisper and the AI are CPU heavy; keep them off the circuit's thread so the page stays responsive.
                var text = await Task.Run(() => SpeechToText.TranscribeAsync(samples, knownNames));
                if (string.IsNullOrWhiteSpace(text))
                {
                    status = "I couldn't make out any words. Try again a little closer to the microphone.";
                    return;
                }
                AddLog("you", "record_voice_over", "You said", $"“{text}”");

                SetStatus("Understanding what you meant…");
                var actions = await Executor.UnderstandAsync(text);   // waits on Ollama over HTTP; stays on the circuit because it uses the page's DbContext

                var commands = actions.Where(a => a.Type != VoiceAction.Unknown).ToList();
                if (commands.Count > 0)
                {
                    var understood = AddLog("info", "psychology", commands.Count == 1 ? "I understood" : $"I understood {commands.Count} things", null);
                    understood.Details.AddRange(commands.Select(VoiceCommandExecutor.Describe));

                    var fixes = commands.SelectMany(a => VoiceCommandExecutor.SpellingFixes(text, a)).ToList();
                    if (fixes.Count > 0)
                    {
                        var fixEntry = AddLog("fix", "spellcheck", "Fixed what I heard", null);
                        fixEntry.Details.AddRange(fixes.Select(f => $"You said “{f.Heard}” → I used “{f.Used}”"));
                    }
                    SetStatus("Saving…");
                }

                var results = await Executor.RunAsync(actions, Screen);
                foreach (var result in results)
                {
                    var entry = AddLog(KindOf(result.Kind), IconOf(result.Kind), result.Title, result.Message);
                    entry.Details.AddRange(result.Details);
                    entry.Link = result.Link;
                    entry.LinkText = result.LinkText;
                }

                var saved = results.Where(r => r.Kind == NotificationSeverity.Success).ToList();
                if (saved.Count > 0)
                {
                    await OnChanged.InvokeAsync();
                    NotificationService.Notify(new NotificationMessage() { Severity = NotificationSeverity.Success, Summary = saved.Count == 1 ? saved[0].Title : $"{saved.Count} records saved", Detail = string.Join(" · ", saved.Select(r => r.Message)), Duration = 5000 });
                }
                status = $"Done in {watch.Elapsed.TotalSeconds:0.0} s — listening for your next command.";
            }
            catch (Exception ex)
            {
                AddLog("error", "error", "Something went wrong", ex.Message,
                    ex.Message.Contains("localhost:11434") || ex is System.Net.Http.HttpRequestException ? "Is Ollama running? Open it from the Windows Start menu." : null);
                status = "Listening — try again.";
            }
            finally
            {
                if (IsOn)
                {
                    state = VoiceState.Listening;
                }
                phraseLock.Release();
                StateHasChanged();
            }
        }

        private void SetStatus(string text)
        {
            status = text;
            InvokeAsync(StateHasChanged);
        }

        private LogEntry AddLog(string kind, string icon, string title, string text, string tip = null)
        {
            var entry = new LogEntry() { Kind = kind, Icon = icon, Title = title, Text = text };
            if (tip != null)
            {
                entry.Details.Add(tip);
            }
            log.Add(entry);
            if (log.Count > MaxLogEntries)
            {
                log.RemoveAt(0);
            }
            scrollPending = true;
            InvokeAsync(StateHasChanged);
            return entry;
        }

        private static string KindOf(NotificationSeverity severity) => severity switch
        {
            NotificationSeverity.Success => "success",
            NotificationSeverity.Warning => "warning",
            NotificationSeverity.Error => "error",
            _ => "info"
        };

        private static string IconOf(NotificationSeverity severity) => severity switch
        {
            NotificationSeverity.Success => "check_circle",
            NotificationSeverity.Warning => "warning",
            NotificationSeverity.Error => "error",
            _ => "info"
        };

        public async ValueTask DisposeAsync()
        {
            if (IsOn)
            {
                state = VoiceState.Off;
                try
                {
                    await JSRuntime.InvokeVoidAsync("voiceCommands.stop");
                }
                catch (JSDisconnectedException)
                {
                    // The browser tab is already gone, so the microphone is already released.
                }
            }
            selfRef?.Dispose();
        }
    }
}
