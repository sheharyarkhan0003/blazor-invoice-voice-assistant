using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Whisper.net;
using Whisper.net.Ggml;

namespace SimpleInvoiceManagementSoftware
{
    /// <summary>
    /// Offline speech-to-text using Whisper.net (whisper.cpp). Free and open source: no paid API is called.
    /// The model file is downloaded once from Hugging Face on first use and then loaded from disk.
    /// </summary>
    public class SpeechToTextService : IDisposable
    {
        // Gives Whisper the vocabulary of commands (brand names, units). It is a plain list, not a command,
        // so if Whisper ever echoes the prompt on silence the AI sees no instruction in it.
        private const string DefaultPrompt = "Products: Surf Excel, Tata Salt, Amul Butter, Colgate, Maggi. Sizes in kg, grams, litre, ml. Prices in rupees.";

        private readonly string prompt;
        private readonly SemaphoreSlim loadLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim processLock = new SemaphoreSlim(1, 1);
        private readonly ILogger<SpeechToTextService> logger;
        private readonly GgmlType modelType;
        private readonly string modelPath;
        private WhisperFactory factory;

        public SpeechToTextService(IConfiguration configuration, IWebHostEnvironment env, ILogger<SpeechToTextService> logger)
        {
            this.logger = logger;
            modelType = Enum.TryParse(configuration["Whisper:ModelType"], true, out GgmlType type) ? type : GgmlType.BaseEn;
            var folder = Path.Combine(env.ContentRootPath, configuration["Whisper:ModelFolder"] ?? "WhisperModels");
            modelPath = Path.Combine(folder, $"ggml-{modelType}.bin");
            prompt = string.IsNullOrWhiteSpace(configuration["Whisper:Prompt"]) ? DefaultPrompt : configuration["Whisper:Prompt"];
        }

        public bool IsModelReady => factory != null;

        public async Task EnsureModelLoadedAsync(CancellationToken cancellationToken = default)
        {
            if (factory != null)
            {
                return;
            }

            await loadLock.WaitAsync(cancellationToken);
            try
            {
                if (factory != null)
                {
                    return;
                }

                if (!File.Exists(modelPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(modelPath));
                    logger.LogInformation("Downloading Whisper model {ModelType} to {ModelPath}", modelType, modelPath);

                    var tempPath = modelPath + ".download";
                    using (var model = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(modelType, QuantizationType.NoQuantization, cancellationToken))
                    using (var file = File.Create(tempPath))
                    {
                        await model.CopyToAsync(file, cancellationToken);
                    }
                    File.Move(tempPath, modelPath, true);
                }

                factory = WhisperFactory.FromPath(modelPath);
                logger.LogInformation("Whisper model {ModelType} loaded", modelType);
            }
            finally
            {
                loadLock.Release();
            }
        }

        /// <summary>
        /// Transcribes 16 kHz mono PCM samples (range -1..1) to English text.
        /// knownNames (e.g. existing products and customers) help Whisper spell those names correctly.
        /// </summary>
        public async Task<string> TranscribeAsync(float[] samples, IEnumerable<string> knownNames = null, CancellationToken cancellationToken = default)
        {
            await EnsureModelLoadedAsync(cancellationToken);

            // Whisper only reads the last ~220 tokens of the prompt, so keep the name list short.
            var fullPrompt = prompt;
            var names = string.Join(", ", (knownNames ?? Enumerable.Empty<string>()).Distinct());
            if (names.Length > 0)
            {
                fullPrompt += " Names: " + (names.Length > 500 ? names.Substring(0, 500) : names) + ".";
            }

            // One transcription at a time: whisper.cpp already uses several CPU threads per run.
            await processLock.WaitAsync(cancellationToken);
            try
            {
                using (var processor = factory.CreateBuilder().WithLanguage("en").WithPrompt(fullPrompt).Build())
                {
                    var text = new StringBuilder();
                    await foreach (var segment in processor.ProcessAsync(samples, cancellationToken))
                    {
                        text.Append(segment.Text);
                    }

                    // Drop markers such as [BLANK_AUDIO] or (music) that Whisper emits for non-speech.
                    return Regex.Replace(text.ToString(), @"\[[^\]]*\]|\([^)]*\)", " ").Trim();
                }
            }
            finally
            {
                processLock.Release();
            }
        }

        public void Dispose()
        {
            factory?.Dispose();
        }
    }
}
