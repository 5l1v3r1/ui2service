﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using turbojpegCLI;

namespace UI2Service
{
	public class JpegDiffVideoException : Exception
	{
		public JpegDiffVideoException(string message)
			: base(message)
		{
		}
	}
	public class JpegDiffVideoEncoder : IDisposable
	{
		public static int[] Versions = JpegDiffVideoDecoder.Versions;
		/// <summary>
		/// A VideoDecoder instance that will synchronously decode the video stream as we encode it.  This allows the encoder to update its 'back buffer' (previousFrameData) with the exact same image that a remote client would see.  Otherwise, lossy compression artifacts would add up and quickly lead to a hideous image in many circumstances.
		/// </summary>
		JpegDiffVideoDecoder decoder;
		private byte[] clientVisibleFrame = null;
		private byte[] diffFrame = null;
		private byte[] returnDataBuffer = null;
		TJDecompressor decomp;
		TJCompressor comp;
		private int compressionQuality = 80;
		/// <summary>
		/// The compression quality from 1 to 100.
		/// </summary>
		public int CompressionQuality
		{
			get { return compressionQuality; }
			set
			{
				if (value < 1)
					compressionQuality = 1;
				else if (value > 100)
					compressionQuality = 100;
				else
					compressionQuality = value;
			}
		}
		Thread decoderThread;
		EventWaitHandle waitBeforeStartingDecode = new EventWaitHandle(false, EventResetMode.ManualReset);
		EventWaitHandle waitBeforeStartingEncode = new EventWaitHandle(true, EventResetMode.ManualReset);
		volatile int lastArguedVersion;

		volatile bool isDisposed = false;

		public JpegDiffVideoEncoder()
		{
			decoder = new JpegDiffVideoDecoder();
			decomp = new TJDecompressor();
			comp = new TJCompressor();
			decoderThread = new Thread(decoderThreadLoop);
			decoderThread.IsBackground = true;
			decoderThread.Name = "JpegDiffVideoEncoder Decoder Thread";
			decoderThread.Start();
		}
		#region IDisposable Members

		/// <summary>
		/// Call this when finished with the instance. If using a C# using block, you won't need to call this.
		/// </summary>
		public void Dispose()
		{
			try
			{
				lock (this)
				{
					if (isDisposed)
						return;
					isDisposed = true;
					decoder.Dispose();
					decomp.Dispose();
					comp.Dispose();
				}
			}
			catch (Exception)
			{
				Console.WriteLine("Exception disposing TurboJpeg stuff in JpegDiffVideoEncoder");
			}
		}

		#endregion
		/// <summary>
		/// Accepts a jpeg frame and encodes the corresponding JpegDiff video frame.  This instance reserves ownership of the returned byte array, and may return the same byte array again later after changing its content.  You can use the byte array for any purpose until the next call to EncodeFrame on this instance.
		/// </summary>
		/// <param name="jpegData">A byte array containing a jpeg image.</param>
		/// <param name="inputSizeBytes">The length of the input data (probably jpegData.Length).</param>
		/// <param name="outputSizeBytes">This variable will be set to the length of the jpeg data in the returned array. Assume the returned array is actually longer than this value.</param>
		/// <returns></returns>
		public byte[] EncodeFrame(byte[] jpegData, int inputSizeBytes, out int outputSizeBytes, int version)
		{
			if (!Versions.Contains(version))
			{
				outputSizeBytes = 0;
				return new byte[0];
			}
			lock (this)
			{
				if (isDisposed)
				{
					outputSizeBytes = 0;
					return new byte[0];
				}
				if (clientVisibleFrame == null)
				{
					// Special case: First frame of video.
					clientVisibleFrame = decoder.DecodeFrame(jpegData, inputSizeBytes, version);
					outputSizeBytes = jpegData.Length;
					return jpegData;

				}
				decomp.setSourceImage(jpegData, inputSizeBytes);
				if (decoder.Width != decomp.getWidth() || decoder.Height != decomp.getHeight())
					throw new JpegDiffVideoException("New frame dimensions (" + decomp.getWidth() + "x" + decomp.getHeight() + ") do not match the first frame (" + decoder.Width + "x" + decoder.Height + ")");

				if (diffFrame == null)
				{
					diffFrame = new byte[clientVisibleFrame.Length];
				}

				decomp.decompress(diffFrame);

				try
				{
					waitBeforeStartingEncode.WaitOne();
					// We will now iterate through the pixels, one color channel at a time (each byte is one color channel for one pixel)
					// When we are done iterating, the [bitmap] object will contain an image representing the difference between the 
					// live (previous) frame and the upcoming (new) frame.
					//
					// We are shooting for a low contrast image here, as this will compress the best.
					byte[] encoderArray;
					if (version == 1)
						encoderArray = encoderArrayV1; // V1 clamps extreme values to -128 or 128.  Color quality is maintained, but sharp color transitions suffer.
					else if (version == 2)
						encoderArray = encoderArrayV2; // V2 simply cuts color depth in half.
					else if (version == 3)
						encoderArray = encoderArrayV3; // V3 compresses the full color range into half the space, giving priority to small changes.
					else if (version == 4)
						encoderArray = encoderArrayV4; // V4 compresses the full color range into the full space, giving priority to small changes.
					else
						throw new Exception("Invalid version number specified");

					for (int i = 0; i < diffFrame.Length; i++)
						diffFrame[i] = encoderArray[255 + ((int)diffFrame[i] - (int)clientVisibleFrame[i])];

					lastArguedVersion = version;

					// The diff frame has been calculated.  Now compress it.
					comp.setSourceImage(diffFrame, decoder.Width, decoder.Height);
					comp.setJPEGQuality(compressionQuality);
					if (returnDataBuffer == null)
						returnDataBuffer = comp.compress();
					else
						comp.compress(ref returnDataBuffer, Flag.NONE);
					outputSizeBytes = comp.getCompressedSize();

					//comp.setSourceImage(previousFrameData, decoder.Width, decoder.Height);
					//returnDataBuffer = comp.compress();
					//outputSizeBytes = comp.getCompressedSize();
					return returnDataBuffer;
				}
				finally
				{
					waitBeforeStartingEncode.Reset();
					waitBeforeStartingDecode.Set();
				}
			}
		}
		private static int Clamp(int i, int min, int max)
		{
			if (i < min)
				return min;
			if (i > max)
				return max;
			return i;
		}
		private void decoderThreadLoop()
		{
			try
			{
				while (!isDisposed)
				{
					try
					{
						waitBeforeStartingDecode.WaitOne();
						if (isDisposed)
						{
							waitBeforeStartingEncode.Set();
							return;
						}
						waitBeforeStartingDecode.Reset();
						clientVisibleFrame = decoder.DecodeFrame(returnDataBuffer, comp.getCompressedSize(), lastArguedVersion);
						waitBeforeStartingEncode.Set();
					}
					catch (ThreadAbortException ex)
					{
						throw ex;
					}
					catch (Exception ex)
					{
						Console.WriteLine(ex.ToString());
						waitBeforeStartingEncode.Set();
					}
				}
			}
			catch (ThreadAbortException)
			{
			}
			catch (Exception ex)
			{
				Console.WriteLine(ex.ToString());
			}
			finally
			{
				waitBeforeStartingEncode.Set();
			}
		}
		private static byte[] encoderArrayV1 = new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48, 49, 50, 51, 52, 53, 54, 55, 56, 57, 58, 59, 60, 61, 62, 63, 64, 65, 66, 67, 68, 69, 70, 71, 72, 73, 74, 75, 76, 77, 78, 79, 80, 81, 82, 83, 84, 85, 86, 87, 88, 89, 90, 91, 92, 93, 94, 95, 96, 97, 98, 99, 100, 101, 102, 103, 104, 105, 106, 107, 108, 109, 110, 111, 112, 113, 114, 115, 116, 117, 118, 119, 120, 121, 122, 123, 124, 125, 126, 127, 128, 129, 130, 131, 132, 133, 134, 135, 136, 137, 138, 139, 140, 141, 142, 143, 144, 145, 146, 147, 148, 149, 150, 151, 152, 153, 154, 155, 156, 157, 158, 159, 160, 161, 162, 163, 164, 165, 166, 167, 168, 169, 170, 171, 172, 173, 174, 175, 176, 177, 178, 179, 180, 181, 182, 183, 184, 185, 186, 187, 188, 189, 190, 191, 192, 193, 194, 195, 196, 197, 198, 199, 200, 201, 202, 203, 204, 205, 206, 207, 208, 209, 210, 211, 212, 213, 214, 215, 216, 217, 218, 219, 220, 221, 222, 223, 224, 225, 226, 227, 228, 229, 230, 231, 232, 233, 234, 235, 236, 237, 238, 239, 240, 241, 242, 243, 244, 245, 246, 247, 248, 249, 250, 251, 252, 253, 254, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255 };
		private static byte[] encoderArrayV2 = new byte[] { 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13, 14, 14, 15, 15, 16, 16, 17, 17, 18, 18, 19, 19, 20, 20, 21, 21, 22, 22, 23, 23, 24, 24, 25, 25, 26, 26, 27, 27, 28, 28, 29, 29, 30, 30, 31, 31, 32, 32, 33, 33, 34, 34, 35, 35, 36, 36, 37, 37, 38, 38, 39, 39, 40, 40, 41, 41, 42, 42, 43, 43, 44, 44, 45, 45, 46, 46, 47, 47, 48, 48, 49, 49, 50, 50, 51, 51, 52, 52, 53, 53, 54, 54, 55, 55, 56, 56, 57, 57, 58, 58, 59, 59, 60, 60, 61, 61, 62, 62, 63, 63, 64, 64, 65, 65, 66, 66, 67, 67, 68, 68, 69, 69, 70, 70, 71, 71, 72, 72, 73, 73, 74, 74, 75, 75, 76, 76, 77, 77, 78, 78, 79, 79, 80, 80, 81, 81, 82, 82, 83, 83, 84, 84, 85, 85, 86, 86, 87, 87, 88, 88, 89, 89, 90, 90, 91, 91, 92, 92, 93, 93, 94, 94, 95, 95, 96, 96, 97, 97, 98, 98, 99, 99, 100, 100, 101, 101, 102, 102, 103, 103, 104, 104, 105, 105, 106, 106, 107, 107, 108, 108, 109, 109, 110, 110, 111, 111, 112, 112, 113, 113, 114, 114, 115, 115, 116, 116, 117, 117, 118, 118, 119, 119, 120, 120, 121, 121, 122, 122, 123, 123, 124, 124, 125, 125, 126, 126, 127, 127, 128, 128, 129, 129, 130, 130, 131, 131, 132, 132, 133, 133, 134, 134, 135, 135, 136, 136, 137, 137, 138, 138, 139, 139, 140, 140, 141, 141, 142, 142, 143, 143, 144, 144, 145, 145, 146, 146, 147, 147, 148, 148, 149, 149, 150, 150, 151, 151, 152, 152, 153, 153, 154, 154, 155, 155, 156, 156, 157, 157, 158, 158, 159, 159, 160, 160, 161, 161, 162, 162, 163, 163, 164, 164, 165, 165, 166, 166, 167, 167, 168, 168, 169, 169, 170, 170, 171, 171, 172, 172, 173, 173, 174, 174, 175, 175, 176, 176, 177, 177, 178, 178, 179, 179, 180, 180, 181, 181, 182, 182, 183, 183, 184, 184, 185, 185, 186, 186, 187, 187, 188, 188, 189, 189, 190, 190, 191, 191, 192, 192, 193, 193, 194, 194, 195, 195, 196, 196, 197, 197, 198, 198, 199, 199, 200, 200, 201, 201, 202, 202, 203, 203, 204, 204, 205, 205, 206, 206, 207, 207, 208, 208, 209, 209, 210, 210, 211, 211, 212, 212, 213, 213, 214, 214, 215, 215, 216, 216, 217, 217, 218, 218, 219, 219, 220, 220, 221, 221, 222, 222, 223, 223, 224, 224, 225, 225, 226, 226, 227, 227, 228, 228, 229, 229, 230, 230, 231, 231, 232, 232, 233, 233, 234, 234, 235, 235, 236, 236, 237, 237, 238, 238, 239, 239, 240, 240, 241, 241, 242, 242, 243, 243, 244, 244, 245, 245, 246, 246, 247, 247, 248, 248, 249, 249, 250, 250, 251, 251, 252, 252, 253, 253, 254, 254, 255 };
		private static byte[] encoderArrayV3 = new byte[] { 64, 64, 64, 64, 65, 65, 65, 65, 65, 65, 65, 65, 66, 66, 66, 66, 66, 66, 66, 66, 67, 67, 67, 67, 67, 67, 67, 67, 68, 68, 68, 68, 68, 68, 68, 68, 69, 69, 69, 69, 69, 69, 69, 69, 70, 70, 70, 70, 70, 70, 70, 70, 71, 71, 71, 71, 71, 71, 71, 71, 72, 72, 72, 72, 72, 72, 72, 72, 73, 73, 73, 73, 73, 73, 73, 73, 74, 74, 74, 74, 74, 74, 74, 74, 75, 75, 75, 75, 75, 75, 75, 75, 76, 76, 76, 76, 76, 76, 76, 76, 77, 77, 77, 77, 77, 77, 77, 77, 78, 78, 78, 78, 78, 78, 78, 78, 79, 79, 79, 79, 79, 79, 79, 79, 80, 80, 80, 80, 80, 80, 81, 81, 81, 82, 82, 82, 82, 82, 83, 83, 83, 84, 84, 84, 84, 84, 85, 85, 85, 86, 86, 86, 86, 86, 87, 87, 87, 88, 88, 88, 88, 88, 89, 89, 89, 90, 90, 90, 90, 90, 91, 91, 91, 92, 92, 92, 92, 92, 93, 93, 93, 94, 94, 94, 94, 94, 95, 95, 95, 96, 96, 96, 96, 97, 97, 97, 98, 98, 98, 99, 99, 99, 100, 100, 100, 101, 101, 101, 102, 102, 102, 103, 103, 103, 104, 104, 104, 105, 105, 105, 106, 106, 106, 107, 107, 107, 108, 108, 108, 109, 109, 109, 110, 110, 110, 111, 111, 111, 112, 112, 113, 114, 115, 116, 117, 118, 119, 120, 121, 122, 123, 124, 125, 126, 127, 128, 129, 130, 131, 132, 133, 134, 135, 136, 137, 138, 139, 140, 141, 142, 143, 144, 144, 145, 145, 145, 146, 146, 146, 147, 147, 147, 148, 148, 148, 149, 149, 149, 150, 150, 150, 151, 151, 151, 152, 152, 152, 153, 153, 153, 154, 154, 154, 155, 155, 155, 156, 156, 156, 157, 157, 157, 158, 158, 158, 159, 159, 159, 160, 160, 160, 160, 161, 161, 161, 162, 162, 162, 162, 162, 163, 163, 163, 164, 164, 164, 164, 164, 165, 165, 165, 166, 166, 166, 166, 166, 167, 167, 167, 168, 168, 168, 168, 168, 169, 169, 169, 170, 170, 170, 170, 170, 171, 171, 171, 172, 172, 172, 172, 172, 173, 173, 173, 174, 174, 174, 174, 174, 175, 175, 175, 176, 176, 176, 176, 176, 176, 177, 177, 177, 177, 177, 177, 177, 177, 178, 178, 178, 178, 178, 178, 178, 178, 179, 179, 179, 179, 179, 179, 179, 179, 180, 180, 180, 180, 180, 180, 180, 180, 181, 181, 181, 181, 181, 181, 181, 181, 182, 182, 182, 182, 182, 182, 182, 182, 183, 183, 183, 183, 183, 183, 183, 183, 184, 184, 184, 184, 184, 184, 184, 184, 185, 185, 185, 185, 185, 185, 185, 185, 186, 186, 186, 186, 186, 186, 186, 186, 187, 187, 187, 187, 187, 187, 187, 187, 188, 188, 188, 188, 188, 188, 188, 188, 189, 189, 189, 189, 189, 189, 189, 189, 190, 190, 190, 190, 190, 190, 190, 190, 191, 191, 191, 191, 191, 191, 191, 191, 192, 192, 192, 192 };
		private static byte[] encoderArrayV4 = new byte[] { 0, 0, 1, 1, 1, 2, 2, 2, 3, 3, 3, 4, 4, 4, 5, 5, 5, 6, 6, 6, 7, 7, 7, 8, 8, 8, 9, 9, 9, 10, 10, 10, 11, 11, 11, 12, 12, 12, 13, 13, 13, 14, 14, 14, 15, 15, 15, 16, 16, 16, 17, 17, 17, 18, 18, 18, 19, 19, 19, 20, 20, 20, 21, 21, 21, 22, 22, 22, 23, 23, 23, 24, 24, 24, 25, 25, 25, 26, 26, 26, 27, 27, 27, 28, 28, 28, 29, 29, 29, 30, 30, 30, 31, 31, 31, 32, 32, 32, 32, 33, 33, 33, 34, 34, 34, 35, 35, 35, 36, 36, 36, 37, 37, 37, 38, 38, 38, 39, 39, 39, 40, 40, 40, 41, 41, 41, 42, 42, 42, 43, 43, 43, 44, 44, 44, 45, 45, 45, 46, 46, 46, 47, 47, 47, 48, 48, 48, 49, 49, 49, 50, 50, 50, 51, 51, 51, 52, 52, 52, 53, 53, 53, 54, 54, 54, 55, 55, 55, 56, 56, 56, 57, 57, 57, 58, 58, 58, 59, 59, 59, 60, 60, 60, 61, 61, 61, 62, 62, 62, 63, 63, 64, 65, 66, 67, 68, 69, 70, 71, 72, 73, 74, 75, 76, 77, 78, 79, 80, 81, 82, 83, 84, 85, 86, 87, 88, 89, 90, 91, 92, 93, 94, 95, 96, 97, 98, 99, 100, 101, 102, 103, 104, 105, 106, 107, 108, 109, 110, 111, 112, 113, 114, 115, 116, 117, 118, 119, 120, 121, 122, 123, 124, 125, 126, 127, 128, 129, 130, 131, 132, 133, 134, 135, 136, 137, 138, 139, 140, 141, 142, 143, 144, 145, 146, 147, 148, 149, 150, 151, 152, 153, 154, 155, 156, 157, 158, 159, 160, 161, 162, 163, 164, 165, 166, 167, 168, 169, 170, 171, 172, 173, 174, 175, 176, 177, 178, 179, 180, 181, 182, 183, 184, 185, 186, 187, 188, 189, 190, 191, 192, 193, 193, 194, 194, 194, 195, 195, 195, 196, 196, 196, 197, 197, 197, 198, 198, 198, 199, 199, 199, 200, 200, 200, 201, 201, 201, 201, 202, 202, 202, 203, 203, 203, 204, 204, 204, 205, 205, 205, 206, 206, 206, 207, 207, 207, 208, 208, 208, 209, 209, 209, 210, 210, 210, 211, 211, 211, 212, 212, 212, 213, 213, 213, 214, 214, 214, 215, 215, 215, 216, 216, 216, 216, 217, 217, 217, 218, 218, 218, 219, 219, 219, 220, 220, 220, 221, 221, 221, 222, 222, 222, 223, 223, 223, 224, 224, 224, 225, 225, 225, 226, 226, 226, 227, 227, 227, 228, 228, 228, 229, 229, 229, 230, 230, 230, 231, 231, 231, 232, 232, 232, 232, 233, 233, 233, 234, 234, 234, 235, 235, 235, 236, 236, 236, 237, 237, 237, 238, 238, 238, 239, 239, 239, 240, 240, 240, 241, 241, 241, 242, 242, 242, 243, 243, 243, 244, 244, 244, 245, 245, 245, 246, 246, 246, 247, 247, 247, 247, 248, 248, 248, 249, 249, 249, 250, 250, 250, 251, 251, 251, 252, 252, 252, 253, 253, 253, 254, 254, 254, 255, 255 };
	}
}
