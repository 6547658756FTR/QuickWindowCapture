using System.Diagnostics;

namespace QuickWindowCapture
{
    // Some macro tools and games occasionally omit the matching key-up signal.
    // A quiet interval lets the next distinct press recover without treating
    // normal key-repeat messages as additional capture requests.
    internal sealed class CaptureKeyLatch
    {
        internal const int RecoverySilenceMilliseconds = 750;

        private bool pressed;
        private long lastSignalTimestamp;

        internal bool TryPress()
        {
            return TryPress(Stopwatch.GetTimestamp());
        }

        internal bool TryPress(long timestamp)
        {
            if (pressed && HasRecoverySilenceElapsed(timestamp))
            {
                pressed = false;
            }

            lastSignalTimestamp = timestamp;
            if (pressed)
            {
                return false;
            }

            pressed = true;
            return true;
        }

        internal void Release()
        {
            Release(Stopwatch.GetTimestamp());
        }

        internal void Release(long timestamp)
        {
            pressed = false;
            lastSignalTimestamp = timestamp;
        }

        internal void Reset()
        {
            pressed = false;
            lastSignalTimestamp = 0;
        }

        private bool HasRecoverySilenceElapsed(long timestamp)
        {
            if (lastSignalTimestamp == 0 || timestamp < lastSignalTimestamp)
            {
                return true;
            }

            long elapsed = timestamp - lastSignalTimestamp;
            return elapsed * 1000 / Stopwatch.Frequency >= RecoverySilenceMilliseconds;
        }
    }
}
