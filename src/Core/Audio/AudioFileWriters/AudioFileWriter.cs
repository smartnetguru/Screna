﻿using System;
using System.IO;
using static System.Text.Encoding;

namespace Screna.Audio
{
    /// <summary>
    /// Writes an Audio file.
    /// </summary>
    public class AudioFileWriter : IAudioFileWriter
    {
        readonly object _syncLock = new object();
        readonly BinaryWriter _writer;
        readonly WaveFormat _format;

        readonly long _dataSizePos, _factSampleCountPos;

        readonly bool _riff;
        readonly IAudioEncoder _encoder;
        byte[] _encodedBuffer;

        /// <summary>
        /// Creates a new instance of <see cref="AudioFileWriter"/>.
        /// </summary>
        public AudioFileWriter(Stream OutStream, WaveFormat Format, bool Riff = true)
        {
            if (OutStream == null)
                throw new ArgumentNullException(nameof(OutStream));

            if (Format == null)
                throw new ArgumentNullException(nameof(Format));

            _format = Format;
            _riff = Riff;

            _writer = new BinaryWriter(OutStream, UTF8);

            if (Riff)
            {
                _writer.Write(UTF8.GetBytes("RIFF"));
                _writer.Write(0); // placeholder
                _writer.Write(UTF8.GetBytes("WAVE"));

                _writer.Write(UTF8.GetBytes("fmt "));

                _writer.Write(18 + Format.ExtraSize); // wave format length
            }

            Format.Serialize(_writer);

            if (!Riff)
                return;

            // CreateFactChunk
            if (HasFactChunk)
            {
                _writer.Write(UTF8.GetBytes("fact"));
                _writer.Write(4);
                _factSampleCountPos = OutStream.Position;
                _writer.Write(0); // number of samples
            }

            // WriteDataChunkHeader
            _writer.Write(UTF8.GetBytes("data"));
            _dataSizePos = OutStream.Position;
            _writer.Write(0); // placeholder
        }

        /// <summary>
        /// Creates a new instance of <see cref="AudioFileWriter"/>.
        /// </summary>
        public AudioFileWriter(string FileName, WaveFormat Format, bool Riff = true)
            : this(new FileStream(FileName, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read), Format, Riff) { }

        /// <summary>
        /// Creates a new instance of <see cref="AudioFileWriter"/>.
        /// </summary>
        public AudioFileWriter(Stream OutStream, IAudioEncoder Encoder)
            : this(OutStream, Encoder.WaveFormat, Encoder.RequiresRiffHeader)
        {
            _encoder = Encoder;
        }

        /// <summary>
        /// Creates a new instance of <see cref="AudioFileWriter"/>.
        /// </summary>
        public AudioFileWriter(string FileName, IAudioEncoder Encoder)
            : this(new FileStream(FileName, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read), Encoder) { }

        bool HasFactChunk => _format.Encoding != WaveFormatEncoding.Pcm && _format.BitsPerSample != 0;
        
        /// <summary>
        /// Number of bytes of audio in the data chunk
        /// </summary>
        public long Length { get; private set; }

        /// <summary>
        /// Writes to file.
        /// </summary>
        public void Write(byte[] Data, int Offset, int Count)
        {
            lock (_syncLock)
            {
                if (_riff && _writer.BaseStream.Length + Count > uint.MaxValue)
                    throw new ArgumentException("WAV file too large", nameof(Count));

                if (_encoder != null)
                {
                    _encoder.EnsureBufferIsSufficient(ref _encodedBuffer, Count);

                    var encodedLength = _encoder.Encode(Data, Offset, Count, _encodedBuffer, 0);

                    if (encodedLength <= 0)
                        return;

                    _writer.Write(_encodedBuffer, 0, encodedLength);
                    Length += encodedLength;
                }
                else
                {
                    _writer.Write(Data, Offset, Count);
                    Length += Count;
                }
            }
        }

        /// <summary>
        /// Writes all buffered data to file.
        /// </summary>
        public void Flush()
        {
            lock (_syncLock)
            {
                _writer.Flush();

                if (_encoder != null)
                {
                    // Flush the encoder
                    _encoder.EnsureBufferIsSufficient(ref _encodedBuffer, 0);

                    var encodedLength = _encoder.Flush(_encodedBuffer, 0);

                    if (encodedLength > 0)
                    {
                        _writer.Write(_encodedBuffer, 0, encodedLength);
                        Length += encodedLength;
                    }
                }

                if (!_riff)
                    return;

                var pos = _writer.BaseStream.Position;
                UpdateHeader();
                _writer.BaseStream.Position = pos;
            }
        }
        
        /// <summary>
        /// Updates the header with file size information
        /// </summary>
        void UpdateHeader()
        {
            // UpdateRiffChunk
            _writer.Seek(4, SeekOrigin.Begin);
            _writer.Write((uint)(_writer.BaseStream.Length - 8));

            // UpdateFactChunk
            if (HasFactChunk)
            {
                var bitsPerSample = _format.BitsPerSample * _format.Channels;
                if (bitsPerSample != 0)
                {
                    _writer.Seek((int)_factSampleCountPos, SeekOrigin.Begin);

                    _writer.Write((int)(Length * 8 / bitsPerSample));
                }
            }

            // UpdateDataChunk
            _writer.Seek((int)_dataSizePos, SeekOrigin.Begin);
            _writer.Write((uint)Length);
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            using (_writer)
                Flush();

            _encoder?.Dispose();
        }
    }
}