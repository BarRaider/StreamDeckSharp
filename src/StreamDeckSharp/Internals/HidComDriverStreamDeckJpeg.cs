using OpenMacroBoard.SDK;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using System;
using System.IO;

namespace StreamDeckSharp.Internals
{
    /// <summary>
    /// Specifies how key images are transformed before sending to the device.
    /// </summary>
    public enum JpegImageTransform
    {
        /// <summary>
        /// No transformation applied. Used by Stream Deck Plus.
        /// </summary>
        None,

        /// <summary>
        /// Rotate 90 degrees clockwise.
        /// </summary>
        Rotate90,

        /// <summary>
        /// Rotate 180 degrees (flip both axes). Used by MK.2, Rev2, XL, Neo.
        /// </summary>
        Rotate180,

        /// <summary>
        /// Rotate 270 degrees clockwise (90 degrees counter-clockwise).
        /// </summary>
        Rotate270,

        /// <summary>
        /// Mirror horizontally (flip left-right).
        /// </summary>
        FlipH,

        /// <summary>
        /// Mirror horizontally then rotate 90 degrees clockwise.
        /// </summary>
        FlipHRotate90,

        /// <summary>
        /// Mirror vertically (flip top-bottom).
        /// </summary>
        FlipV,

        /// <summary>
        /// Transpose (mirror along top-left to bottom-right diagonal).
        /// </summary>
        Transpose,
    }

    /// <summary>
    /// HID Stream Deck communication driver for JPEG based devices.
    /// </summary>
    public sealed class HidComDriverStreamDeckJpeg
        : IStreamDeckHidComDriver
    {
        private readonly int imgSize;
        private readonly int expectedInputReportLength;
        private JpegImageTransform imageTransform;
        private readonly JpegEncoder jpgEncoder;

        private byte[] cachedNullImage = null;

        /// <summary>
        /// Initializes a new instance of the <see cref="HidComDriverStreamDeckJpeg"/> class.
        /// </summary>
        /// <param name="imgSize">The size of the button images in pixels.</param>
        /// <param name="expectedInputReportLength">The expected input report length for the device.</param>
        /// <param name="imageTransform">The image transformation to apply before sending to the device.</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown if the <paramref name="imgSize"/> is smaller than one.</exception>
        public HidComDriverStreamDeckJpeg(
            int imgSize,
            int expectedInputReportLength = 512,
            JpegImageTransform imageTransform = JpegImageTransform.Rotate180
        )
        {
            if (imgSize < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(imgSize));
            }

            jpgEncoder = new JpegEncoder()
            {
                Quality = 100,
            };

            this.imgSize = imgSize;
            this.expectedInputReportLength = expectedInputReportLength;
            this.imageTransform = imageTransform;
        }

        /// <inheritdoc/>
        public int HeaderSize => 8;

        /// <inheritdoc/>
        public int ReportSize => 1024;

        /// <inheritdoc/>
        public int ExpectedFeatureReportLength => 32;

        /// <inheritdoc/>
        public int ExpectedOutputReportLength => 1024;

        /// <inheritdoc/>
        public int ExpectedInputReportLength => expectedInputReportLength;

        /// <inheritdoc/>
        public int KeyReportOffset => 4;

        /// <inheritdoc/>
        public byte FirmwareVersionFeatureId => 5;

        /// <inheritdoc/>
        public byte SerialNumberFeatureId => 6;

        /// <inheritdoc/>
        public int FirmwareVersionReportSkip => 6;

        /// <inheritdoc/>
        public int SerialNumberReportSkip => 2;

        /// <inheritdoc/>
        public double BytesPerSecondLimit { get; set; } = double.PositiveInfinity;

        internal void SetImageTransform(JpegImageTransform transform)
        {
            imageTransform = transform;
            cachedNullImage = null;
        }

        /// <inheritdoc/>
        public int ExtKeyIdToHardwareKeyId(int extKeyId)
        {
            return extKeyId;
        }

        /// <inheritdoc/>
        public byte[] GeneratePayload(KeyBitmap keyBitmap)
        {
            var rawData = keyBitmap.GetScaledVersion(imgSize, imgSize);

            if (rawData.Length == 0)
            {
                return GetNullImage();
            }

            return EncodeImageToJpg(rawData);
        }

        /// <inheritdoc/>
        public int HardwareKeyIdToExtKeyId(int hardwareKeyId)
        {
            return hardwareKeyId;
        }

        /// <inheritdoc/>
        public void PrepareDataForTransmission(
            byte[] data,
            int pageNumber,
            int payloadLength,
            int keyId,
            bool isLast
        )
        {
            data[0] = 2;
            data[1] = 7;
            data[2] = (byte)keyId;
            data[3] = (byte)(isLast ? 1 : 0);
            data[4] = (byte)(payloadLength & 255);
            data[5] = (byte)(payloadLength >> 8);
            data[6] = (byte)pageNumber;
        }

        /// <inheritdoc/>
        public byte[] GetBrightnessMessage(byte percent)
        {
            if (percent > 100)
            {
                throw new ArgumentOutOfRangeException(nameof(percent));
            }

            var buffer = new byte[]
            {
                0x03, 0x08, 0x64, 0x23, 0xB8, 0x01, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0xA5, 0x49, 0xCD, 0x02, 0xFE, 0x7F, 0x00, 0x00,
            };

            buffer[2] = percent;
            buffer[3] = 0x23;  // 0x23, sometimes 0x27

            return buffer;
        }

        /// <inheritdoc/>
        public byte[] GetLogoMessage()
        {
            return new byte[] { 0x03, 0x02 };
        }

        private byte[] GetNullImage()
        {
            if (cachedNullImage is null)
            {
                var rawNullImg = KeyBitmap.Create.FromBgr24Array(1, 1, new byte[] { 0, 0, 0 }).GetScaledVersion(imgSize, imgSize);
                cachedNullImage = EncodeImageToJpg(rawNullImg);
            }

            return cachedNullImage;
        }

        private byte[] EncodeImageToJpg(ReadOnlySpan<byte> bgr24)
        {
            var transformedData = new byte[imgSize * imgSize * 3];

            for (var y = 0; y < imgSize; y++)
            {
                for (var x = 0; x < imgSize; x++)
                {
                    int srcX, srcY;

                    var m = imgSize - 1;

                    switch (imageTransform)
                    {
                        case JpegImageTransform.Rotate90:
                            srcX = y;
                            srcY = m - x;
                            break;
                        case JpegImageTransform.Rotate180:
                            srcX = m - x;
                            srcY = m - y;
                            break;
                        case JpegImageTransform.Rotate270:
                            srcX = m - y;
                            srcY = x;
                            break;
                        case JpegImageTransform.FlipH:
                            srcX = m - x;
                            srcY = y;
                            break;
                        case JpegImageTransform.FlipHRotate90:
                            srcX = m - y;
                            srcY = m - x;
                            break;
                        case JpegImageTransform.FlipV:
                            srcX = x;
                            srcY = m - y;
                            break;
                        case JpegImageTransform.Transpose:
                            srcX = y;
                            srcY = x;
                            break;
                        default: // None
                            srcX = x;
                            srcY = y;
                            break;
                    }

                    var pTarget = (y * imgSize + x) * 3;
                    var pSource = (srcY * imgSize + srcX) * 3;

                    transformedData[pTarget + 0] = bgr24[pSource + 0];
                    transformedData[pTarget + 1] = bgr24[pSource + 1];
                    transformedData[pTarget + 2] = bgr24[pSource + 2];
                }
            }

            using var image = Image.LoadPixelData<Bgr24>(transformedData, imgSize, imgSize);

            using var memStream = new MemoryStream();
            image.SaveAsJpeg(memStream, jpgEncoder);

            return memStream.ToArray();
        }
    }
}
