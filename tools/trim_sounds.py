import argparse
import ctypes
import json
import os
import wave
from pathlib import Path

import numpy as np


MPG123_OK = 0
MPG123_NEW_FORMAT = -11
MPG123_DONE = -12


class Mpg123Decoder:
    def __init__(self, library_path: Path):
        self.library = ctypes.CDLL(str(library_path))
        self.library.mpg123_init.restype = ctypes.c_int
        self.library.mpg123_new.argtypes = [ctypes.c_char_p, ctypes.POINTER(ctypes.c_int)]
        self.library.mpg123_new.restype = ctypes.c_void_p
        self.library.mpg123_open.argtypes = [ctypes.c_void_p, ctypes.c_char_p]
        self.library.mpg123_open.restype = ctypes.c_int
        self.library.mpg123_getformat.argtypes = [
            ctypes.c_void_p,
            ctypes.POINTER(ctypes.c_long),
            ctypes.POINTER(ctypes.c_int),
            ctypes.POINTER(ctypes.c_int),
        ]
        self.library.mpg123_getformat.restype = ctypes.c_int
        self.library.mpg123_outblock.argtypes = [ctypes.c_void_p]
        self.library.mpg123_outblock.restype = ctypes.c_size_t
        self.library.mpg123_read.argtypes = [
            ctypes.c_void_p,
            ctypes.POINTER(ctypes.c_ubyte),
            ctypes.c_size_t,
            ctypes.POINTER(ctypes.c_size_t),
        ]
        self.library.mpg123_read.restype = ctypes.c_int
        self.library.mpg123_encsize.argtypes = [ctypes.c_int]
        self.library.mpg123_encsize.restype = ctypes.c_int
        self.library.mpg123_strerror.argtypes = [ctypes.c_void_p]
        self.library.mpg123_strerror.restype = ctypes.c_char_p
        self.library.mpg123_delete.argtypes = [ctypes.c_void_p]
        self.library.mpg123_close.argtypes = [ctypes.c_void_p]
        self.library.mpg123_exit.restype = None

        result = self.library.mpg123_init()
        if result != MPG123_OK:
            raise RuntimeError(f"mpg123_init failed: {result}")

    def close(self):
        self.library.mpg123_exit()

    def decode(self, file_path: Path):
        error = ctypes.c_int()
        handle = self.library.mpg123_new(None, ctypes.byref(error))
        if not handle:
            raise RuntimeError(f"mpg123_new failed: {error.value}")

        try:
            relative_path = os.path.relpath(file_path, Path.cwd())
            result = self.library.mpg123_open(handle, os.fsencode(relative_path))
            if result != MPG123_OK:
                raise RuntimeError(self._error_text(handle, result))

            rate = ctypes.c_long()
            channels = ctypes.c_int()
            encoding = ctypes.c_int()
            result = self.library.mpg123_getformat(
                handle, ctypes.byref(rate), ctypes.byref(channels), ctypes.byref(encoding)
            )
            if result != MPG123_OK:
                raise RuntimeError(self._error_text(handle, result))

            encoding_size = self.library.mpg123_encsize(encoding.value)
            block_size = max(int(self.library.mpg123_outblock(handle)), 16384)
            buffer = (ctypes.c_ubyte * block_size)()
            chunks = []

            while True:
                bytes_read = ctypes.c_size_t()
                result = self.library.mpg123_read(
                    handle, buffer, block_size, ctypes.byref(bytes_read)
                )
                if bytes_read.value:
                    chunks.append(bytes(buffer[: bytes_read.value]))

                if result == MPG123_DONE:
                    break
                if result not in (MPG123_OK, MPG123_NEW_FORMAT):
                    raise RuntimeError(self._error_text(handle, result))
                if result == MPG123_NEW_FORMAT:
                    new_rate = ctypes.c_long()
                    new_channels = ctypes.c_int()
                    new_encoding = ctypes.c_int()
                    self.library.mpg123_getformat(
                        handle,
                        ctypes.byref(new_rate),
                        ctypes.byref(new_channels),
                        ctypes.byref(new_encoding),
                    )
                    if (
                        new_rate.value != rate.value
                        or new_channels.value != channels.value
                        or new_encoding.value != encoding.value
                    ):
                        raise RuntimeError("The MP3 output format changed while decoding.")

            raw = b"".join(chunks)
            if encoding_size == 2:
                samples = np.frombuffer(raw, dtype="<i2").astype(np.float32) / 32768.0
            elif encoding_size == 4 and encoding.value & 0x200:
                samples = np.frombuffer(raw, dtype="<f4").astype(np.float32)
            elif encoding_size == 4:
                samples = np.frombuffer(raw, dtype="<i4").astype(np.float32) / 2147483648.0
            elif encoding_size == 1:
                samples = (np.frombuffer(raw, dtype=np.uint8).astype(np.float32) - 128.0) / 128.0
            else:
                raise RuntimeError(f"Unsupported mpg123 sample size: {encoding_size}")

            if samples.size % channels.value:
                raise RuntimeError("Decoded sample data is not channel-aligned.")
            return samples.reshape((-1, channels.value)), int(rate.value)
        finally:
            self.library.mpg123_close(handle)
            self.library.mpg123_delete(handle)

    def _error_text(self, handle, result):
        message = self.library.mpg123_strerror(handle)
        if message:
            return f"mpg123 error {result}: {message.decode(errors='replace')}"
        return f"mpg123 error {result}"


def find_trim_bounds(samples, sample_rate, threshold_db, leading_ms, trailing_ms):
    mono_peak = np.max(np.abs(samples), axis=1)
    peak = float(np.max(mono_peak))
    if peak <= 0.0:
        return 0, len(samples), peak, 0.0

    window_frames = max(1, int(round(sample_rate * 0.004)))
    squared = np.square(mono_peak.astype(np.float64))
    cumulative = np.concatenate(([0.0], np.cumsum(squared)))
    window_energy = cumulative[window_frames:] - cumulative[:-window_frames]
    rms = np.sqrt(window_energy / window_frames)

    relative_threshold = peak * (10.0 ** (threshold_db / 20.0))
    absolute_floor = 10.0 ** (-70.0 / 20.0)
    threshold = max(relative_threshold, absolute_floor)
    active = np.flatnonzero(rms >= threshold)
    if active.size == 0:
        return 0, len(samples), peak, threshold

    leading_frames = int(round(sample_rate * leading_ms / 1000.0))
    trailing_frames = int(round(sample_rate * trailing_ms / 1000.0))
    start = max(0, int(active[0]) - leading_frames)
    end = min(
        len(samples),
        int(active[-1]) + window_frames + trailing_frames,
    )
    return start, end, peak, threshold


def write_wave(file_path, samples, sample_rate):
    pcm = np.clip(np.rint(samples * 32767.0), -32768, 32767).astype("<i2")
    file_path.parent.mkdir(parents=True, exist_ok=True)
    with wave.open(str(file_path), "wb") as output:
        output.setnchannels(samples.shape[1])
        output.setsampwidth(2)
        output.setframerate(sample_rate)
        output.writeframes(pcm.tobytes())


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("input", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument(
        "--decoder",
        type=Path,
        default=Path(r"C:\Program Files\Audacity\mpg123.dll"),
    )
    parser.add_argument("--threshold-db", type=float, default=-55.0)
    parser.add_argument("--leading-ms", type=float, default=3.0)
    parser.add_argument("--trailing-ms", type=float, default=12.0)
    parser.add_argument("--analyze-only", action="store_true")
    args = parser.parse_args()

    input_files = sorted(args.input.glob("*.mp3"))
    if not input_files:
        raise RuntimeError(f"No MP3 files found in {args.input}")

    decoder = Mpg123Decoder(args.decoder)
    report = []
    try:
        for input_file in input_files:
            samples, sample_rate = decoder.decode(input_file.resolve())
            start, end, peak, threshold = find_trim_bounds(
                samples,
                sample_rate,
                args.threshold_db,
                args.leading_ms,
                args.trailing_ms,
            )
            output_file = args.output / (input_file.stem + ".wav")
            if not args.analyze_only:
                write_wave(output_file, samples[start:end], sample_rate)

            report.append(
                {
                    "file": input_file.name,
                    "sample_rate": sample_rate,
                    "channels": int(samples.shape[1]),
                    "original_ms": round(len(samples) * 1000.0 / sample_rate, 2),
                    "trimmed_ms": round((end - start) * 1000.0 / sample_rate, 2),
                    "leading_removed_ms": round(start * 1000.0 / sample_rate, 2),
                    "trailing_removed_ms": round((len(samples) - end) * 1000.0 / sample_rate, 2),
                    "peak": round(peak, 6),
                    "threshold": round(threshold, 6),
                    "output": None if args.analyze_only else str(output_file),
                }
            )
    finally:
        decoder.close()

    print(json.dumps(report, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
