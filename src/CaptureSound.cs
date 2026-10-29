using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace QuickWindowCapture
{
    internal sealed class CaptureSoundOption
    {
        internal string Id { get; private set; }
        internal string DisplayName { get; private set; }
        internal string ResourceFileName { get; private set; }

        internal bool IsBuiltIn
        {
            get { return string.IsNullOrEmpty(ResourceFileName); }
        }

        internal CaptureSoundOption(string id, string displayName, string resourceFileName)
        {
            Id = id;
            DisplayName = displayName;
            ResourceFileName = resourceFileName;
        }

        public override string ToString()
        {
            return DisplayName;
        }
    }

    internal sealed class CaptureSound : IDisposable
    {
        internal const string DefaultOptionId = "builtin";

        private const string ResourcePrefix = "QuickWindowCapture.Sounds.";
        private const int MaximumOverlappingPlayers = 8;
        private static readonly CaptureSoundOption[] SoundOptions =
        {
            new CaptureSoundOption(DefaultOptionId, "효과음 1", null),
            new CaptureSoundOption("decision_52", "효과음 2", "decision_52.wav"),
            new CaptureSoundOption("decision_51", "효과음 3", "decision_51.wav"),
            new CaptureSoundOption("decision_45", "효과음 4", "decision_45.wav"),
            new CaptureSoundOption("decision_35", "효과음 5", "decision_35.wav"),
            new CaptureSoundOption("decision_13", "효과음 6", "decision_13.wav"),
            new CaptureSoundOption("cursor_11", "효과음 7", "cursor_11.wav"),
            new CaptureSoundOption("cursor_10", "효과음 8", "cursor_10.wav"),
            new CaptureSoundOption("cursor_8", "효과음 9", "cursor_8.wav"),
            new CaptureSoundOption("cursor_5", "효과음 10", "cursor_5.wav"),
            new CaptureSoundOption("shutter_2", "효과음 11", "shutter_2.wav")
        };

        private readonly object syncRoot = new object();
        private readonly byte[] builtInWave;
        private readonly List<System.Windows.Media.MediaPlayer> activePlayers;
        private readonly Dictionary<string, FileStream> soundFileLocks;
        private bool disposed;

        internal CaptureSound()
        {
            builtInWave = CreateShutterWave();
            activePlayers = new List<System.Windows.Media.MediaPlayer>();
            soundFileLocks = new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase);
        }

        internal static CaptureSoundOption[] GetOptions()
        {
            return (CaptureSoundOption[])SoundOptions.Clone();
        }

        internal static bool IsValidOptionId(string optionId)
        {
            return FindOption(optionId) != null;
        }

        internal bool Play(string optionId, bool allowOverlap)
        {
            lock (syncRoot)
            {
                if (disposed)
                {
                    return false;
                }

                if (!allowOverlap)
                {
                    StopAllLocked();
                }
                else
                {
                    while (activePlayers.Count >= MaximumOverlappingPlayers)
                    {
                        StopPlayerLocked(activePlayers[0]);
                    }
                }

                CaptureSoundOption option = FindOption(optionId) ?? FindOption(DefaultOptionId);
                System.Windows.Media.MediaPlayer player = new System.Windows.Media.MediaPlayer();
                player.MediaEnded += MediaPlaybackEnded;
                player.MediaFailed += MediaPlaybackFailed;
                activePlayers.Add(player);

                try
                {
                    string filePath = EnsureExtracted(option);
                    player.Open(new Uri(filePath, UriKind.Absolute));
                    player.Play();
                    return true;
                }
                catch
                {
                    StopPlayerLocked(player);
                    return false;
                }
            }
        }

        internal void Stop()
        {
            lock (syncRoot)
            {
                if (!disposed)
                {
                    StopAllLocked();
                }
            }
        }

        private void MediaPlaybackEnded(object sender, EventArgs eventArgs)
        {
            lock (syncRoot)
            {
                StopPlayerLocked(sender as System.Windows.Media.MediaPlayer);
            }
        }

        private void MediaPlaybackFailed(object sender, System.Windows.Media.ExceptionEventArgs eventArgs)
        {
            lock (syncRoot)
            {
                StopPlayerLocked(sender as System.Windows.Media.MediaPlayer);
            }
        }

        private static CaptureSoundOption FindOption(string optionId)
        {
            foreach (CaptureSoundOption option in SoundOptions)
            {
                if (string.Equals(option.Id, optionId, StringComparison.OrdinalIgnoreCase))
                {
                    return option;
                }
            }

            return null;
        }

        private string EnsureExtracted(CaptureSoundOption option)
        {
            string soundFolder = Path.Combine(AppSettings.SettingsFolder, "Sounds", "1.6.2");
            string fileName = option.IsBuiltIn ? "builtin.wav" : option.ResourceFileName;
            string filePath = Path.Combine(soundFolder, fileName);

            FileStream existingLock;
            if (soundFileLocks.TryGetValue(option.Id, out existingLock))
            {
                return filePath;
            }

            Stream soundStream = option.IsBuiltIn
                ? (Stream)new MemoryStream(builtInWave, false)
                : Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourcePrefix + option.ResourceFileName);

            using (soundStream)
            {
                if (soundStream == null)
                {
                    throw new InvalidOperationException("효과음 리소스를 찾지 못했습니다.");
                }

                Directory.CreateDirectory(soundFolder);
                using (FileStream output = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    soundStream.CopyTo(output);
                    output.Flush(true);
                }
            }

            FileStream readLock = null;
            try
            {
                readLock = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                soundFileLocks.Add(option.Id, readLock);
            }
            catch
            {
                if (readLock != null)
                {
                    readLock.Dispose();
                }
                throw;
            }

            return filePath;
        }

        private void StopAllLocked()
        {
            while (activePlayers.Count > 0)
            {
                StopPlayerLocked(activePlayers[activePlayers.Count - 1]);
            }
        }

        private void StopPlayerLocked(System.Windows.Media.MediaPlayer player)
        {
            if (player == null)
            {
                return;
            }

            player.MediaEnded -= MediaPlaybackEnded;
            player.MediaFailed -= MediaPlaybackFailed;
            try
            {
                player.Stop();
                player.Close();
            }
            catch
            {
            }

            activePlayers.Remove(player);
        }

        private static byte[] CreateShutterWave()
        {
            const int sampleRate = 22050;
            const double durationSeconds = 0.14;
            int sampleCount = (int)(sampleRate * durationSeconds);
            short[] samples = new short[sampleCount];
            Random noise = new Random(48271);

            for (int index = 0; index < sampleCount; index++)
            {
                double time = (double)index / sampleRate;
                double value = 0;

                if (time < 0.038)
                {
                    double envelope = Math.Exp(-time * 72.0);
                    value += envelope * ((noise.NextDouble() * 2.0 - 1.0) * 0.62);
                    value += envelope * Math.Sin(2.0 * Math.PI * 1850.0 * time) * 0.24;
                }

                if (time >= 0.052 && time < 0.13)
                {
                    double localTime = time - 0.052;
                    double envelope = Math.Exp(-localTime * 42.0);
                    value += envelope * ((noise.NextDouble() * 2.0 - 1.0) * 0.48);
                    value += envelope * Math.Sin(2.0 * Math.PI * 980.0 * localTime) * 0.18;
                }

                value = Math.Max(-1.0, Math.Min(1.0, value));
                samples[index] = (short)(value * short.MaxValue * 0.72);
            }

            int dataSize = sampleCount * sizeof(short);
            using (MemoryStream output = new MemoryStream(44 + dataSize))
            using (BinaryWriter writer = new BinaryWriter(output))
            {
                writer.Write(new[] { 'R', 'I', 'F', 'F' });
                writer.Write(36 + dataSize);
                writer.Write(new[] { 'W', 'A', 'V', 'E' });
                writer.Write(new[] { 'f', 'm', 't', ' ' });
                writer.Write(16);
                writer.Write((short)1);
                writer.Write((short)1);
                writer.Write(sampleRate);
                writer.Write(sampleRate * sizeof(short));
                writer.Write((short)sizeof(short));
                writer.Write((short)16);
                writer.Write(new[] { 'd', 'a', 't', 'a' });
                writer.Write(dataSize);
                foreach (short sample in samples)
                {
                    writer.Write(sample);
                }
                writer.Flush();
                return output.ToArray();
            }
        }

        public void Dispose()
        {
            lock (syncRoot)
            {
                if (disposed)
                {
                    return;
                }

                StopAllLocked();
                foreach (FileStream soundFileLock in soundFileLocks.Values)
                {
                    soundFileLock.Dispose();
                }
                soundFileLocks.Clear();
                disposed = true;
            }
        }
    }
}
