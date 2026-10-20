// Voice commands: records the microphone as 16 kHz mono audio, splits it into phrases with a
// simple silence detector, and sends each phrase to .NET, where Whisper turns it into text.
window.voiceCommands = (function () {
    var SAMPLE_RATE = 16000;     // what Whisper expects
    var PRE_ROLL_MS = 300;       // audio kept from just before speech starts
    var END_SILENCE_MS = 900;    // this much silence ends a phrase
    var MIN_SPEECH_MS = 300;     // shorter sounds (clicks, coughs) are ignored
    var MAX_PHRASE_MS = 15000;   // force-send very long phrases

    var state = null;

    function level(samples) {
        var sum = 0;
        for (var i = 0; i < samples.length; i++) {
            sum += samples[i] * samples[i];
        }
        return Math.sqrt(sum / samples.length);
    }

    // Returns a copy at 16 kHz (the browser reuses the input buffer, so it must be copied anyway).
    function resample(input, ratio) {
        if (ratio === 1) {
            return new Float32Array(input);
        }
        var output = new Float32Array(Math.floor(input.length / ratio));
        for (var i = 0; i < output.length; i++) {
            var start = Math.floor(i * ratio), end = Math.min(Math.floor((i + 1) * ratio), input.length), sum = 0;
            for (var j = start; j < end; j++) {
                sum += input[j];
            }
            output[i] = sum / Math.max(end - start, 1);
        }
        return output;
    }

    function durationMs(chunks) {
        var length = 0;
        for (var i = 0; i < chunks.length; i++) {
            length += chunks[i].length;
        }
        return length / SAMPLE_RATE * 1000;
    }

    function sendPhrase(s) {
        var chunks = s.chunks, speechMs = s.speechMs;
        s.chunks = [];
        s.speaking = false;
        s.speechMs = 0;
        s.silenceMs = 0;
        if (speechMs < MIN_SPEECH_MS) {
            return;
        }

        var length = 0;
        chunks.forEach(function (c) { length += c.length; });
        var phrase = new Float32Array(length), offset = 0;
        chunks.forEach(function (c) { phrase.set(c, offset); offset += c.length; });

        s.dotnetRef.invokeMethodAsync('OnVoicePhrase', new Uint8Array(phrase.buffer))
            .catch(function (e) { console.error('voiceCommands: sending audio failed', e); });
    }

    function onAudio(s, input) {
        var samples = resample(input, s.ratio);
        var chunkMs = samples.length / SAMPLE_RATE * 1000;
        var volume = level(samples);
        var threshold = Math.max(0.015, s.noise * 3);

        // Live level bar, so the user can see the microphone is hearing them.
        if (s.meter) {
            s.meter.style.width = Math.min(100, Math.round(volume / 0.15 * 100)) + '%';
            s.meter.classList.toggle('speaking', volume > threshold);
        }

        if (!s.speaking) {
            s.noise = s.noise * 0.95 + volume * 0.05;   // follow the background noise level
            s.chunks.push(samples);
            while (s.chunks.length > 1 && durationMs(s.chunks) > PRE_ROLL_MS) {
                s.chunks.shift();
            }
            if (volume > threshold) {
                s.speaking = true;
                s.speechMs = chunkMs;
                s.dotnetRef.invokeMethodAsync('OnSpeechStarted').catch(function () { });
            }
            return;
        }

        s.chunks.push(samples);
        if (volume > threshold) {
            s.speechMs += chunkMs;
            s.silenceMs = 0;
        } else {
            s.silenceMs += chunkMs;
        }
        if (s.silenceMs >= END_SILENCE_MS || durationMs(s.chunks) >= MAX_PHRASE_MS) {
            sendPhrase(s);
        }
    }

    // meter (optional): an element whose width shows the live microphone level.
    async function start(dotnetRef, meter) {
        if (state) {
            return;
        }

        var stream = await navigator.mediaDevices.getUserMedia({
            audio: { channelCount: 1, echoCancellation: true, noiseSuppression: true }
        });

        var context, source;
        try {
            context = new AudioContext({ sampleRate: SAMPLE_RATE });
            source = context.createMediaStreamSource(stream);
        } catch (e) {
            // Firefox cannot connect a mic to a 16 kHz context; record at the device rate and resample.
            if (context) {
                context.close();
            }
            context = new AudioContext();
            source = context.createMediaStreamSource(stream);
        }
        await context.resume();

        var processor = context.createScriptProcessor(1024, 1, 1);
        var s = {
            dotnetRef: dotnetRef, meter: meter, stream: stream, context: context, source: source, processor: processor,
            ratio: context.sampleRate / SAMPLE_RATE, noise: 0.01,
            chunks: [], speaking: false, speechMs: 0, silenceMs: 0
        };
        processor.onaudioprocess = function (e) {
            onAudio(s, e.inputBuffer.getChannelData(0));
        };
        source.connect(processor);
        processor.connect(context.destination);   // required for onaudioprocess to fire; output stays silent
        state = s;
    }

    function stop() {
        if (!state) {
            return;
        }
        var s = state;
        state = null;
        s.processor.onaudioprocess = null;
        s.source.disconnect();
        s.processor.disconnect();
        s.stream.getTracks().forEach(function (t) { t.stop(); });
        s.context.close();
        if (s.meter) {
            s.meter.style.width = '0%';
            s.meter.classList.remove('speaking');
        }
    }

    // Keeps the newest activity visible.
    function scrollToBottom(element) {
        if (element) {
            element.scrollTop = element.scrollHeight;
        }
    }

    return { start: start, stop: stop, scrollToBottom: scrollToBottom };
})();
