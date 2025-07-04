﻿using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using static System.Net.Mime.MediaTypeNames;
using static System.Runtime.InteropServices.JavaScript.JSType;

/// <summary>
/// Creates a GIF using .Net GIF encoding and additional animation headers.
/// </summary>

public class GifWriter
{
    #region Fields
    const long SourceGlobalColorInfoPosition = 10, SourceImageBlockPosition = 789;

    readonly BinaryWriter _writer;
    bool _firstFrame = true;
    readonly object _syncLock = new object();
    #endregion
    public GifWriter(Stream OutStream, int DefaultFrameDelay = 5000, int Repeat = 20)
    {
        if (OutStream == null)
            throw new ArgumentNullException("OutStream");

        if (DefaultFrameDelay <= 0)
            throw new ArgumentOutOfRangeException("DefaultFrameDelay");

        if (Repeat < -1)
            throw new ArgumentOutOfRangeException("Repeat");

        _writer = new BinaryWriter(OutStream);
        this.DefaultFrameDelay = DefaultFrameDelay;
        this.Repeat = Repeat;
    }

    /// <summary>
    /// Creates a new instance of GifWriter.
    /// </summary>
    /// <param name="FileName">The path to the file to output the Gif to.</param>
    /// <param name="DefaultFrameDelay">Default Delay between consecutive frames... FrameRate = 1000 / DefaultFrameDelay.</param>
    /// <param name="Repeat">No of times the Gif should repeat... -1 to repeat indefinitely.</param>
    public GifWriter(string FileName, int DefaultFrameDelay = 500, int Repeat = 20000)
        : this(new FileStream(FileName, FileMode.Create), DefaultFrameDelay, Repeat) { }

    #region Properties
    /// <summary>
    /// Gets or Sets the Default Width of a Frame. Used when unspecified.
    /// </summary>
    public int DefaultWidth { get; set; }

    /// <summary>
    /// Gets or Sets the Default Height of a Frame. Used when unspecified.
    /// </summary>
    public int DefaultHeight { get; set; }

    /// <summary>
    /// Gets or Sets the Default Delay in Milliseconds.
    /// </summary>
    public int DefaultFrameDelay { get; set; }

    /// <summary>
    /// The Number of Times the Animation must repeat.
    /// -1 indicates no repeat. 0 indicates repeat indefinitely
    /// </summary>
    public int Repeat { get; set; }
    #endregion

    /// <summary>
    /// Adds a frame to this animation.
    /// </summary>
    /// <param name="Image">The image to add</param>
    /// <param name="Delay">Delay in Milliseconds between this and last frame... 0 = <see cref="DefaultFrameDelay"/></param>
    public void WriteFrame(System.Drawing.Image Image, int Delay = 0)
    {
        lock (_syncLock)
            using (var gifStream = new MemoryStream())
            {
                Image.Save(gifStream, ImageFormat.Gif);

                // Steal the global color table info
                if (_firstFrame)
                    InitHeader(gifStream, _writer, Image.Width, Image.Height);

                WriteGraphicControlBlock(gifStream, _writer, Delay == 0 ? DefaultFrameDelay : Delay);
                WriteImageBlock(gifStream, _writer, !_firstFrame, 0, 0, Image.Width, Image.Height);
            }

        if (_firstFrame)
            _firstFrame = false;
    }

    #region Write
    void InitHeader(Stream SourceGif, BinaryWriter Writer, int Width, int Height)
    {
        // File Header
        Writer.Write("GIF".ToCharArray()); // File type
        Writer.Write("89a".ToCharArray()); // File Version

        Writer.Write((short)(DefaultWidth == 0 ? Width : DefaultWidth)); // Initial Logical Width
        Writer.Write((short)(DefaultHeight == 0 ? Height : DefaultHeight)); // Initial Logical Height

        SourceGif.Position = SourceGlobalColorInfoPosition;
        Writer.Write((byte)SourceGif.ReadByte()); // Global Color Table Info
        Writer.Write((byte)0); // Background Color Index
        Writer.Write((byte)0); // Pixel aspect ratio
        WriteColorTable(SourceGif, Writer);

        // App Extension Header for Repeating
        if (Repeat == -1)
            return;

        Writer.Write(unchecked((short)0xff21)); // Application Extension Block Identifier
        Writer.Write((byte)0x0b); // Application Block Size
        Writer.Write("NETSCAPE2.0".ToCharArray()); // Application Identifier
        Writer.Write((byte)3); // Application block length
        Writer.Write((byte)1);
        Writer.Write((short)Repeat); // Repeat count for images.
        Writer.Write((byte)0); // terminator
    }

    static void WriteColorTable(Stream SourceGif, BinaryWriter Writer)
    {
        SourceGif.Position = 13; // Locating the image color table
        var colorTable = new byte[768];
        SourceGif.Read(colorTable, 0, colorTable.Length);
        Writer.Write(colorTable, 0, colorTable.Length);
    }

    static void WriteGraphicControlBlock(Stream SourceGif, BinaryWriter Writer, int FrameDelay)
    {
        SourceGif.Position = 781; // Locating the source GCE
        var blockhead = new byte[8];
        SourceGif.Read(blockhead, 0, blockhead.Length); // Reading source GCE

        Writer.Write(unchecked((short)0xf921)); // Identifier
        Writer.Write((byte)0x04); // Block Size
        Writer.Write((byte)(blockhead[3] & 0xf7 | 0x08)); // Setting disposal flag
        Writer.Write((short)(FrameDelay / 10)); // Setting frame delay
        Writer.Write(blockhead[6]); // Transparent color index
        Writer.Write((byte)0); // Terminator
    }

    static void WriteImageBlock(Stream SourceGif, BinaryWriter Writer, bool IncludeColorTable, int X, int Y, int Width, int Height)
    {
        SourceGif.Position = SourceImageBlockPosition; // Locating the image block
        var header = new byte[11];
        SourceGif.Read(header, 0, header.Length);
        Writer.Write(header[0]); // Separator
        Writer.Write((short)X); // Position X
        Writer.Write((short)Y); // Position Y
        Writer.Write((short)Width); // Width
        Writer.Write((short)Height); // Height

        if (IncludeColorTable) // If first frame, use global color table - else use local
        {
            SourceGif.Position = SourceGlobalColorInfoPosition;
            Writer.Write((byte)(SourceGif.ReadByte() & 0x3f | 0x80)); // Enabling local color table
            WriteColorTable(SourceGif, Writer);
        }
        else Writer.Write((byte)(header[9] & 0x07 | 0x07)); // Disabling local color table

        Writer.Write(header[10]); // LZW Min Code Size

        // Read/Write image data
        SourceGif.Position = SourceImageBlockPosition + header.Length;

        var dataLength = SourceGif.ReadByte();
        while (dataLength > 0)
        {
            var imgData = new byte[dataLength];
            SourceGif.Read(imgData, 0, dataLength);

            Writer.Write((byte)dataLength);
            Writer.Write(imgData, 0, dataLength);
            dataLength = SourceGif.ReadByte();
        }

        Writer.Write((byte)0); // Terminator
    }
    #endregion

    /// <summary>
    /// Frees all resources used by this object.
    /// </summary>
    public void Dispose()
    {
        // Complete File
        _writer.Write((byte)0x3b); // File Trailer

        _writer.BaseStream.Dispose();
        _writer.Dispose();
    }
}
public class program
{
    public static void Main(string[] Adreses)
    {
        const string nameFile = "input.txt";
        if (Adreses.Length == 0)
        {
            Adreses = File.ReadLines(nameFile).ToArray();
        }
        int numberOfGif = Adreses.Length / 2;
        Console.WriteLine($"for big gif use file named {nameFile}");
        Console.WriteLine("send data througt parameters .example");
        Console.WriteLine("E:\\OneDrive\\Pulpit\\fb activity\\2025.06.18  Will Smith colection\\slides\\f5.png\r\nE:\\OneDrive\\Pulpit\\fb activity\\2025.06.18  Will Smith colection\\slides\\f0.png 6000 1000");
        string[] ScreenShotImagesPaths = Adreses.Take(numberOfGif).ToArray();
        int[] FramesTime = Adreses.Skip(numberOfGif).Take(numberOfGif).Select(X => Convert.ToInt32(X)).ToArray();
        if (Adreses.Length != 0)
        {
            GifWriter gifWriter = new GifWriter(Guid.NewGuid().ToString() + ".gif");
            int index = 0;

            foreach (var image in ScreenShotImagesPaths)
            {

                var imageObject = System.Drawing.Image.FromFile(image.Replace("\"", ""));
                gifWriter.WriteFrame(imageObject, FramesTime[index]);

                imageObject = null;
                index++;
            }

            gifWriter.Dispose();
        }
    }
}
