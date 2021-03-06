﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace Pulsus.FFmpeg
{
	public unsafe class FFmpegContext : IDisposable
	{
		public AVFormatContext* formatContext = null;
		public double framePts { get; private set; }

		public int imageWidth { get { return codecContext->width; } }
		public int imageHeight { get { return codecContext->height; } }
		public AVPixelFormat imagePixelFormat { get { return (AVPixelFormat)format; } }

		public double videoFrametime { get { return (double)codecContext->framerate.den / codecContext->framerate.num; } }
		public double videoDuration
		{
			get
			{
				long duration = formatContext->streams[streamIndex]->duration;
				if ((ulong)duration == ffmpeg.AV_NOPTS_VALUE)
					return 0.0;

				AVRational time_base = formatContext->streams[streamIndex]->time_base;
				return (double)duration * time_base.num / time_base.den;
			}
		}

		public int audioChannels { get { return channels; } }
		public int audioSampleRate { get { return sampleRate; } }
		public AVSampleFormat audioSampleFormat { get { return (AVSampleFormat)format; } }
		public int audioBytesPerSample { get { return ffmpeg.av_get_bytes_per_sample(audioSampleFormat); } }
		public int audioSamplesTotal
		{
			get
			{
				AVStream* stream = formatContext->streams[streamIndex];
				double durationSeconds = (double)stream->duration * stream->time_base.num / stream->time_base.den;
				double samples = durationSeconds * audioSampleRate * audioChannels;
				return (int)samples;
			}
		}
		public int audioBytesTotal { get { return audioSamplesTotal * audioBytesPerSample; } }

		AVIOContext* avioContext = null;
		SwrContext* swrContext = null;
		SwsContext* swsContext = null;
		AVCodecContext* codecContext = null;
		AVFrame* frame = null;
		AVFrame* convertedFrame = null;
		int streamIndex = -1;
		AVMediaType type;

		int format = 0;
		int sampleRate = 0;
		int channels = 0;

		sbyte* convertedBuffer;
		int convertedBytes;
		int convertedMaxSamples;
		int convertedLineSize;

		Stream stream;
		string path;
		sbyte* readBuffer;
		byte[] managedBuffer;

		int decodedFrames;
		int bufferedLength = 0;

		// prevents garbage collector from collecting delegates
		List<Delegate> delegateRefs = new List<Delegate>();

		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		private delegate int ReadStreamDelegate(IntPtr opaque, IntPtr buf, int buf_size);

		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		private delegate int WriteStreamDelegate(IntPtr opaque, IntPtr buf, int buf_size);

		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		private delegate Int64 SeekStreamDelegate(IntPtr opaque, Int64 offset, int whence);


		public static FFmpegContext Read(string path)
		{
			FFmpegContext context = new FFmpegContext(new FileStream(path, FileMode.Open, FileAccess.Read));
			context.OpenInput();

			return context;
		}

		public static FFmpegContext Read(Stream stream)
		{
			FFmpegContext context = new FFmpegContext(stream);
			context.OpenInput();

			return context;
		}

		public static FFmpegContext Write(string path)
		{
			FFmpegContext context = new FFmpegContext(path);
			return context;
		}

		private FFmpegContext(Stream stream)
		{
			this.stream = stream;
		}

		private FFmpegContext(string path)
		{
			this.path = path;
		}

		public void Dispose()
		{
			if (avioContext != null)
				ffmpeg.avio_close(avioContext);

			if (codecContext != null)
				ffmpeg.avcodec_close(codecContext);

			if (formatContext != null)
			{
				if (formatContext->oformat == null)
				{
					fixed (AVFormatContext** ptr = &formatContext)
						ffmpeg.avformat_close_input(ptr);

					ffmpeg.av_free(formatContext);
				}
				else
					ffmpeg.avformat_free_context(formatContext);
			}

			if (swrContext != null)
			{
				ffmpeg.swr_close(swrContext);
				fixed (SwrContext** ptr = &swrContext)
					ffmpeg.swr_free(ptr);
			}

			if (swsContext != null)
				ffmpeg.sws_freeContext(swsContext);

			if (convertedFrame != null)
				fixed (AVFrame** ptr = &convertedFrame)
					ffmpeg.av_frame_free(ptr);

			if (convertedBuffer != null)
				ffmpeg.av_free(convertedBuffer);

			if (frame != null)
				fixed (AVFrame** framePtr = &frame)
					ffmpeg.av_frame_free(framePtr);

			if (stream != null)
				stream.Dispose();

			delegateRefs.Clear();
		}

		private void OpenInput()
		{
			formatContext = ffmpeg.avformat_alloc_context();
			if (stream != null)
			{
				const int bufferSize = 8192;

				// with delegates and anonymous functions, we can capture
				// the local stream variable without passing it as a argument.

				ReadStreamDelegate readStream = (IntPtr opaque, IntPtr buf, int buf_size) =>
				{
					byte[] array = new byte[buf_size];

					int read = stream.Read(array, 0, buf_size);
					for (int i = 0; i < read; i++)
						Marshal.WriteByte(buf, i, array[i]);

					return read;
				};

				SeekStreamDelegate seekStream = (IntPtr opaque, Int64 offset, int whence) =>
				{
					if (whence == ffmpeg.AVSEEK_SIZE)
						return stream.Length;

					if (!stream.CanSeek)
						return -1;

					stream.Seek(offset, (SeekOrigin)whence);
					return stream.Position;
				};

				// track the delegates, GC might remove them prematurely
				delegateRefs.Add(readStream);
				delegateRefs.Add(seekStream);

				// setup custom stream reader for ffmpeg to use with AVIO context
				readBuffer = (sbyte*)ffmpeg.av_malloc(bufferSize + ffmpeg.FF_INPUT_BUFFER_PADDING_SIZE);
				avioContext = FFmpegHelper.avio_alloc_context(readBuffer, bufferSize, 0, null,
					Marshal.GetFunctionPointerForDelegate(readStream), IntPtr.Zero,
					Marshal.GetFunctionPointerForDelegate(seekStream));

				formatContext->pb = avioContext;
				formatContext->flags |= ffmpeg.AVFMT_FLAG_CUSTOM_IO;
			}
			else
			{
				fixed (AVFormatContext** ptr = &formatContext)
					if (ffmpeg.avformat_open_input(ptr, path, null, null) != 0)
						throw new ApplicationException("Failed to open input stream: " + path);
			}

			fixed (AVFormatContext** ptr = &formatContext)
			{
				int err = 0;
				AVInputFormat* inputFormat = null;
				if ((err = ffmpeg.av_probe_input_buffer(formatContext->pb, &inputFormat, path, null, 0, 0)) != 0)
					throw new FFmpegException(err);

				if (path != null && (inputFormat->flags & ffmpeg.AVFMT_NOFILE) != 0)
				{
					// Input format (image2) doesn't support custom AVIOContext,
					// forcefully clear the flag and hope it works.
					inputFormat->flags &= ~ffmpeg.AVFMT_NOFILE;

					//ffmpeg.avio_close(formatContext->pb);
				}

				if ((err = ffmpeg.avformat_open_input(ptr, path, inputFormat, null)) != 0)
					throw new FFmpegException(err);
			}
			frame = ffmpeg.av_frame_alloc();
		}

		// read the media file ahead for proper stream info in cases
		// where the format has no header information.
		public void FindStreamInfo()
		{
			if (ffmpeg.avformat_find_stream_info(formatContext, null) != 0)
				throw new ApplicationException("Failed to find stream info");
		}

		public void SelectStream(AVMediaType type)
		{
			this.type = type;

			streamIndex = ffmpeg.av_find_best_stream(formatContext, type, -1, -1, null, 0);
			if (streamIndex < 0)
				throw new ApplicationException("No stream found for type " + type.ToString());

			SetupCodecContext(formatContext->streams[streamIndex]);
		}

		public void SelectStream(int index)
		{
			if (index >= formatContext->nb_streams)
				throw new ApplicationException("No stream found for index " + index.ToString());

			streamIndex = index;
			SetupCodecContext(formatContext->streams[streamIndex]);
		}

		private void SetupCodecContext(AVStream* stream)
		{
			AVCodec* decoder = ffmpeg.avcodec_find_decoder(stream->codec->codec_id);
			if (decoder == null)
				throw new ApplicationException("No decoder found for " + stream->codec->codec_id.ToString() + " : ");

			codecContext = ffmpeg.avcodec_alloc_context3(decoder);
			if (codecContext == null)
				throw new ApplicationException("Failed to allocate codec context");

			if (ffmpeg.avcodec_copy_context(codecContext, stream->codec) != 0)
				throw new ApplicationException("Failed to copy codec context");

			if (ffmpeg.avcodec_open2(codecContext, decoder, null) < 0)
				throw new ApplicationException("Failed to open decoder for " + codecContext->codec_id.ToString() + " : ");

			channels = codecContext->channels;
			sampleRate = codecContext->sample_rate;

			if (stream->codec->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
				format = (int)codecContext->pix_fmt;
			else if (stream->codec->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
				format = (int)codecContext->sample_fmt;
		}

		public void ConvertToFormat(AVSampleFormat sampleFormat)
		{
			ConvertToFormat(sampleFormat, audioSampleRate, audioChannels);
		}

		public void ConvertToFormat(AVSampleFormat sampleFormat, int sampleRate, int channels)
		{
			if (format == (int)sampleFormat &&
				this.sampleRate == sampleRate &&
				this.channels == channels)
			{
				return;
			}

			format = (int)sampleFormat;
			this.sampleRate = sampleRate;
			this.channels = channels;

			int channelLayout = (int)ffmpeg.av_get_default_channel_layout(channels);

			swrContext = ffmpeg.swr_alloc();
			ffmpeg.av_opt_set_int(swrContext, "in_channel_layout", (int)codecContext->channel_layout, 0);
			ffmpeg.av_opt_set_int(swrContext, "out_channel_layout", channelLayout, 0);
			ffmpeg.av_opt_set_int(swrContext, "in_channel_count", codecContext->channels, 0);
			ffmpeg.av_opt_set_int(swrContext, "out_channel_count", channels, 0);
			ffmpeg.av_opt_set_int(swrContext, "in_sample_rate", codecContext->sample_rate, 0);
			ffmpeg.av_opt_set_int(swrContext, "out_sample_rate", sampleRate, 0);
			ffmpeg.av_opt_set_sample_fmt(swrContext, "in_sample_fmt", codecContext->sample_fmt, 0);
			ffmpeg.av_opt_set_sample_fmt(swrContext, "out_sample_fmt", sampleFormat, 0);

			if (ffmpeg.swr_init(swrContext) != 0)
				throw new ApplicationException("Failed init SwrContext: " + FFmpegHelper.logLastLine);
		}

		public void ConvertToFormat(AVPixelFormat pixelFormat)
		{
			if (codecContext->pix_fmt == pixelFormat)
				return;

			format = (int)pixelFormat;

			convertedFrame = ffmpeg.av_frame_alloc();
			convertedFrame->format = format;
			convertedFrame->width = codecContext->width;
			convertedFrame->height = codecContext->height;
			convertedFrame->channels = 4;

			int swsFlags = 0;

			// fixes weird artifacts at the edges for misaligned dimensions
			if (codecContext->width % 8 != 0 || codecContext->height % 8 != 0)
				swsFlags |= ffmpeg.SWS_ACCURATE_RND;

			swsContext = ffmpeg.sws_getContext(
				codecContext->width, codecContext->height, codecContext->pix_fmt,
				convertedFrame->width, convertedFrame->height, pixelFormat,
				swsFlags, null, null, null);

			// allocate buffers in frame
			if (ffmpeg.av_frame_get_buffer(convertedFrame, 1) != 0)
				throw new ApplicationException("Failed to allocate buffers for frame");
		}

		private bool ReadFrame()
		{
			int decoded = 0;
			AVPacket packet = new AVPacket();
			while (decoded == 0 && ffmpeg.av_read_frame(formatContext, &packet) >= 0)
			{
				if (packet.stream_index == streamIndex)
					decoded = DecodeFrame(packet);

				ffmpeg.av_free_packet(&packet);
			}

			if (decoded == 0)
			{
				// flush out the last frame
				packet.size = 0;
				packet.data = null;
				decoded = DecodeFrame(packet);
			}
			return decoded > 0;
		}

		private int DecodeFrame(AVPacket packet)
		{
			int gotFrame = 0;
			int decodeLength = 0;

			// decode until full frame has been decoded
			do
			{
				if (type == AVMediaType.AVMEDIA_TYPE_VIDEO)
					decodeLength = ffmpeg.avcodec_decode_video2(codecContext, frame, &gotFrame, &packet);
				else if (type == AVMediaType.AVMEDIA_TYPE_AUDIO)
					decodeLength = ffmpeg.avcodec_decode_audio4(codecContext, frame, &gotFrame, &packet);

				if (decodeLength < 0)
				{
					System.Diagnostics.Debug.WriteLine("Failed to decode audio: " + FFmpegHelper.logLastLine);
					break;
				}

				if (gotFrame > 0)
				{
					decodedFrames++;

					AVRational timeBase = formatContext->streams[packet.stream_index]->time_base;

					long pts = ffmpeg.av_frame_get_best_effort_timestamp(frame);
					double currentFrameTime = pts > 0 ? ((double)pts * timeBase.num / timeBase.den) : 0.0;

					if (formatContext->start_time > 0)
						currentFrameTime -= formatContext->start_time / 1000000.0; // AV_TIME_BASE

					// current frame may be repeated multiple times
					currentFrameTime += frame->repeat_pict * (((double)timeBase.num / timeBase.den) * 0.5);

					framePts = currentFrameTime;

					// convert frame data to desired format
					if (swrContext != null || swsContext != null)
						ConvertFrame();

					return 1;
				}

				// offset to next frame in packet
				packet.size -= decodeLength;
				packet.data += decodeLength;

			} while (packet.size > 0);

			// some images cannot be decoded in a single pass
			if (decodeLength > 0 && gotFrame == 0)
				return 0;

			return decodeLength;
		}

		private bool ConvertFrame()
		{
			if (type == AVMediaType.AVMEDIA_TYPE_VIDEO)
			{
				if (ffmpeg.sws_scale(swsContext, &frame->data0, frame->linesize,
					0, codecContext->height, &convertedFrame->data0, convertedFrame->linesize) <= 0)
				{
					throw new ApplicationException("failed to convert frame: " + FFmpegHelper.logLastLine);
				}
			}
			else if (type == AVMediaType.AVMEDIA_TYPE_AUDIO)
			{
				AVFrame* inputFrame = frame;
				if (bufferedLength > 0)
				{
					// passing a null frame flushes the remaining buffered data
					inputFrame = null;
				}

				int targetSamples = (int)ffmpeg.av_rescale_rnd(frame->nb_samples, sampleRate, frame->sample_rate, AVRounding.AV_ROUND_UP);
				if (targetSamples > convertedMaxSamples)
				{
					if (convertedBuffer != null)
					{
						ffmpeg.av_free(convertedBuffer);
						convertedBuffer = null;
					}

					convertedMaxSamples = targetSamples;
					fixed (int* lineSize = &convertedLineSize)
					fixed (sbyte** data = &convertedBuffer)
						ffmpeg.av_samples_alloc(data, lineSize, channels, convertedMaxSamples, (AVSampleFormat)format, 1);
				}
				int samples;
				fixed (sbyte** data = &convertedBuffer)
					samples = ffmpeg.swr_convert(swrContext, data, targetSamples, &frame->data0, frame->nb_samples);
				if (samples < 0)
					throw new ApplicationException("failed to convert frame: " + FFmpegHelper.logLastLine);

				fixed (int* lineSize = &convertedLineSize)
					convertedBytes = ffmpeg.av_samples_get_buffer_size(lineSize, channels, samples, (AVSampleFormat)format, 1);
			}

			return true;
		}

		// returns true if there is data in next frame
		public bool ReadNextFrame()
		{
			// more data in previous frame, continue converting the buffered data
			if (bufferedLength > 0)
				return ConvertFrame();

			return ReadFrame();
		}

		public byte[] GetFrameData()
		{
			sbyte* buffer = null;
			int bufferSize = 0;

			// point to the correct frame where our output is
			AVFrame* dataFrame = frame;
			if (swsContext != null)
				dataFrame = convertedFrame;

			buffer = dataFrame->data0;
			if (type == AVMediaType.AVMEDIA_TYPE_VIDEO)
			{
				bufferSize = ffmpeg.av_image_get_buffer_size((AVPixelFormat)dataFrame->format,
					dataFrame->width, dataFrame->height, 1);
			}
			else if (type == AVMediaType.AVMEDIA_TYPE_AUDIO)
			{
				bufferSize = ffmpeg.av_samples_get_buffer_size(null, audioChannels,
					dataFrame->nb_samples, (AVSampleFormat)dataFrame->format, 1);
			}

			if (swrContext != null)
			{
				buffer = convertedBuffer;
				bufferSize = convertedBytes;
			}

			if (bufferSize < 0)
				throw new ApplicationException("buffer size negative: " + FFmpegHelper.logLastLine);

			// allocate and copy the data to managed memory
			if (managedBuffer == null || managedBuffer.Length != bufferSize)
				managedBuffer = new byte[bufferSize];

			Marshal.Copy((IntPtr)buffer, managedBuffer, 0, bufferSize);

			return managedBuffer;
		}

		// set output format from file extension
		public void SetOutputFormat(AVCodecID codecId, int width, int height, AVPixelFormat pixelFormat, int compression)
		{
			type = AVMediaType.AVMEDIA_TYPE_VIDEO;

			AVOutputFormat* outputFormat = ffmpeg.av_guess_format(null, path, null);
			if (outputFormat == null)
				throw new ApplicationException("Failed to guess output format for extension " + path);

			string name = Marshal.PtrToStringAnsi((IntPtr)outputFormat->name);
			string lname = Marshal.PtrToStringAnsi((IntPtr)outputFormat->long_name);
			string extlist = Marshal.PtrToStringAnsi((IntPtr)outputFormat->extensions);

			if (codecId == AVCodecID.AV_CODEC_ID_NONE)
				codecId = ffmpeg.av_guess_codec(outputFormat, null, path, null, AVMediaType.AVMEDIA_TYPE_VIDEO);

			outputFormat->video_codec = codecId;
			SetupOutput(outputFormat, codecId, width, height, pixelFormat, compression);
		}

		private void SetupOutput(AVOutputFormat* outputFormat, AVCodecID codecId, int width, int height, AVPixelFormat pixelFormat, int compression)
		{
			fixed (AVFormatContext** ptr = &formatContext)
				if (ffmpeg.avformat_alloc_output_context2(ptr, outputFormat, null, path) < 0)
					throw new ApplicationException("Failed to allocate format context");

			formatContext->oformat = outputFormat;

			AVCodec* encoder = ffmpeg.avcodec_find_encoder(codecId);
			if (encoder == null)
				throw new ApplicationException("Failed to load encoder for " + outputFormat->video_codec.ToString());

			codecContext = ffmpeg.avcodec_alloc_context3(encoder);
			if (codecContext == null)
				throw new ApplicationException("Failed to allocate codec context");

			AVStream* videoStream = ffmpeg.avformat_new_stream(formatContext, encoder);
			if (videoStream == null)
				throw new ApplicationException("Failed to create new stream");

			videoStream->id = (int)formatContext->nb_streams - 1;
			streamIndex = videoStream->id;

			codecContext->width = width;
			codecContext->height = height;
			codecContext->compression_level = compression;
			codecContext->pix_fmt = pixelFormat;

			for (int i = 0; encoder->pix_fmts[i] != AVPixelFormat.AV_PIX_FMT_NONE; i++)
			{
				if (encoder->pix_fmts[i] == AVPixelFormat.AV_PIX_FMT_RGBA && pixelFormat == AVPixelFormat.AV_PIX_FMT_BGRA)
				{
					ConvertToFormat(AVPixelFormat.AV_PIX_FMT_RGBA);
					codecContext->pix_fmt = AVPixelFormat.AV_PIX_FMT_RGBA;
					break;
				}
			}

			if (ffmpeg.avcodec_open2(codecContext, encoder, null) < 0)
				throw new ApplicationException("Failed to open codec for " + outputFormat->video_codec.ToString());

			codecContext->codec_tag = 0;
			if ((formatContext->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
				codecContext->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;

			if ((formatContext->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
			{
				fixed (AVIOContext** ptr = &avioContext)
					if (ffmpeg.avio_open(ptr, path, ffmpeg.AVIO_FLAG_WRITE) < 0)
						throw new ApplicationException("Failed to open output file for writing");

				formatContext->pb = avioContext;
			}

			frame = ffmpeg.av_frame_alloc();
			frame->width = codecContext->width;
			frame->height = codecContext->height;
			frame->format = (int)codecContext->pix_fmt;

			if (ffmpeg.av_frame_get_buffer(frame, 1) != 0)
				throw new ApplicationException("Failed to allocate buffers for frame");
		}

		public void SetOutputFormat(AVCodecID codecId, int sampleRate, int sampleCount, AVSampleFormat sampleFormat)
		{
			type = AVMediaType.AVMEDIA_TYPE_AUDIO;

			AVOutputFormat* outputFormat = ffmpeg.av_guess_format(null, path, null);
			if (outputFormat == null)
				throw new ApplicationException("Failed to guess output format for extension " + path);

			string name = Marshal.PtrToStringAnsi((IntPtr)outputFormat->name);
			string lname = Marshal.PtrToStringAnsi((IntPtr)outputFormat->long_name);
			string extlist = Marshal.PtrToStringAnsi((IntPtr)outputFormat->extensions);

			if (codecId == AVCodecID.AV_CODEC_ID_NONE)
				codecId = ffmpeg.av_guess_codec(outputFormat, null, path, null, AVMediaType.AVMEDIA_TYPE_AUDIO);

			outputFormat->audio_codec = codecId;
			SetupOutput(outputFormat, codecId, sampleRate, sampleCount, sampleFormat);
		}

		private void SetupOutput(AVOutputFormat* outputFormat, AVCodecID codecId, int sampleRate, int sampleCount, AVSampleFormat sampleFormat)
		{
			fixed (AVFormatContext** ptr = &formatContext)
				if (ffmpeg.avformat_alloc_output_context2(ptr, outputFormat, null, path) < 0)
					throw new ApplicationException("Failed to allocate format context");

			AVCodec* encoder = ffmpeg.avcodec_find_encoder(codecId);
			if (encoder == null)
				throw new ApplicationException("Failed to load encoder for " + outputFormat->video_codec.ToString());

			codecContext = ffmpeg.avcodec_alloc_context3(encoder);
			if (codecContext == null)
				throw new ApplicationException("Failed to allocate codec context");

			AVStream* audioStream = ffmpeg.avformat_new_stream(formatContext, encoder);
			if (audioStream == null)
				throw new ApplicationException("Failed to create new stream");

			audioStream->id = (int)formatContext->nb_streams - 1;

			codecContext->sample_rate = sampleRate;
			codecContext->channels = 2;
			//codecContext->bit_rate = 64000;
			//codecContext->compression_level = 10;
			codecContext->channel_layout = ffmpeg.AV_CH_LAYOUT_STEREO;
			//codecContext->compression_level = compression;
			codecContext->sample_fmt = sampleFormat;

			if (ffmpeg.avcodec_open2(codecContext, encoder, null) < 0)
				throw new ApplicationException("Failed to open codec for " + outputFormat->video_codec.ToString());

			codecContext->codec_tag = 0;
			if ((formatContext->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
				codecContext->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;

			if ((formatContext->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
			{
				fixed (AVIOContext** ptr = &avioContext)
					if (ffmpeg.avio_open(ptr, path, ffmpeg.AVIO_FLAG_WRITE) < 0)
						throw new ApplicationException("Failed to open output file for writing");

				formatContext->pb = avioContext;
			}

			audioStream->time_base = new AVRational() { num = 1, den = sampleRate };

			frame = ffmpeg.av_frame_alloc();
			frame->sample_rate = codecContext->sample_rate;
			frame->channels = codecContext->channels;
			frame->format = (int)codecContext->sample_fmt;
			frame->channel_layout = codecContext->channel_layout;
			frame->nb_samples = sampleCount;

			if (ffmpeg.av_frame_get_buffer(frame, 1) != 0)
				throw new ApplicationException("Failed to allocate buffers for frame");
		}

		public void WriteHeader()
		{
			if (ffmpeg.avformat_write_header(formatContext, null) < 0)
				throw new ApplicationException("Failed to write header");
		}

		public void WriteFrame(byte[] data)
		{
			Marshal.Copy(data, 0, (IntPtr)frame->data0, data.Length);

			AVPacket packet = new AVPacket();
			packet.data = null;
			packet.size = 0;

			AVFrame* inputFrame = frame;
			if (convertedFrame != null)
			{
				if (!ConvertFrame())
					throw new ApplicationException("Failed to convert frame");
				inputFrame = convertedFrame;
			}

			int gotOutput = 0;
			if (type == AVMediaType.AVMEDIA_TYPE_VIDEO)
			{
				if (ffmpeg.avcodec_encode_video2(codecContext, &packet, inputFrame, &gotOutput) < 0)
					throw new ApplicationException("Failed to encode video");
			}
			else if (type == AVMediaType.AVMEDIA_TYPE_AUDIO)
			{
				if (ffmpeg.avcodec_encode_audio2(codecContext, &packet, inputFrame, &gotOutput) < 0)
					throw new ApplicationException("Failed to encode audio");
			}

			if (gotOutput > 0)
			{
				if (ffmpeg.av_interleaved_write_frame(formatContext, &packet) < 0)
					throw new ApplicationException("Failed av_interleaved_write_frame");
			}
			else
				throw new ApplicationException("No output from avcodec_encode_video2");
		}
	}
}
