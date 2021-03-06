﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using CSCore.Codecs.RAW;
using CSCore.Codecs.WAV;
using DeckLinkAPI;
using Microsoft.Expression.Encoder;
using Microsoft.Expression.Encoder.Devices;
using Microsoft.Expression.Encoder.Live;
using Microsoft.Expression.Encoder.Profiles;
using NReco.VideoConverter;
using OpenTK;
using OpenTK.Graphics.OpenGL;
using OpenTK.Input;
using OpenTK.Platform;

namespace VideoTest
{
  /// <summary>
  /// Raw video output can be viewed with mplayer
  /// e.g., "mplayer c:\Temp\videotest.raw -demuxer rawvideo -rawvideo w=1920:h=1080:uyvy:fps=25"
  /// May need to tweak w / h / fps parameters depending on input format
  /// 
  /// FFMPEG compression
  /// "ffmpeg -f rawvideo -pix_fmt uyvy422 -s:v 1920x1080 -r 25 -i c:\Temp\videotest.raw -c:v libx264 c:\Temp\output.mpg"
  /// </summary>
  public partial class Form1 : Form, IDeckLinkDeviceNotificationCallback, IDeckLinkInputCallback, IDeckLinkScreenPreviewCallback
  {
    private readonly _BMDAudioSampleRate _AudioSampleRate = _BMDAudioSampleRate.bmdAudioSampleRate48kHz;
    private readonly _BMDAudioSampleType _AudioSampleType = _BMDAudioSampleType.bmdAudioSampleType32bitInteger;
    private readonly uint _AudioChannels = 2;
    private readonly uint _AudioSampleDepth = 32;
    
    private readonly IDeckLinkDiscovery _Discovery;
    private readonly IDeckLinkGLScreenPreviewHelper _GLHelper;

    private long _FrameCount = 0;
    private long _PreviewCount = 0;
    private bool _Streaming = false;
    private IDeckLink _DeckLink = null;
    private System.Timers.Timer _GLHack = new System.Timers.Timer();
    private readonly SemaphoreSlim _VideoLock = new SemaphoreSlim(1);

    // Raw video/audio files
    private BinaryWriter _VideoWriter = null;
    private BinaryWriter _AudioWriter = null;

    // NReco.VideoConverter
    private readonly FFMpegConverter _VideoConverter = new FFMpegConverter();
    private FileStream _EncodedStream = null;
    private ConvertLiveMediaTask _EncodeTask = null;

    // Microsoft.Expression.Encoder (Not currently using, just here for reference)
    private LiveJob _Job = null;

    private string _RawVideoFile = "out_video.raw";
    private string _RawAudioFile = "out_audio.raw";
    private string _EncodedVideoFile = "out_video.h264";
    private string _WavAudioFile = "out_audio.wav";
    private string _FinalFile = "out_fin.mp4";

    public Form1()
    {
      InitializeComponent();

      _Discovery = new CDeckLinkDiscovery();
      _GLHelper = new CDeckLinkGLScreenPreviewHelper();
      
      if (_Discovery != null) _Discovery.InstallDeviceNotifications(this);
      
      find.Enabled = false;
      stream.Enabled = false;
      notifications.Text = "Please wait 2 seconds for the preview box to initialise...";

      _GLHack.Interval = TimeSpan.FromSeconds(2).TotalMilliseconds;
      _GLHack.Elapsed += DelayedLoad;
      _GLHack.AutoReset = false;
    }

    private void DelayedLoad(object sender, System.Timers.ElapsedEventArgs e)
    {
      Run(() => 
      {
        find.Enabled = true;
        stream.Enabled = true;
        SetupPreviewBox();
      });
    }
    
    private void Form1_Load(object sender, EventArgs e)
    {
      _GLHack.Start();
    }

    private void Form1_FormClosing(object sender, FormClosingEventArgs e)
    {
      if (_Streaming)
      {
        MessageBox.Show("Kill the streaming first!");
        e.Cancel = true;
        return;
      }

      if (_Discovery != null) _Discovery.UninstallDeviceNotifications();
    }

    private void Run(Action act)
    {
      Invoke(act);
    }

    private string FormatDevice(string display, string model)
    {
      return display == model ? display : string.Format("{0} ({1})", display, model);
    }

    public void DeckLinkDeviceArrived(IDeckLink deckLinkDevice)
    {
      string displayName;
      string modelName;

      deckLinkDevice.GetDisplayName(out displayName);
      deckLinkDevice.GetModelName(out modelName);
      
      Run(() => notifications.Text = string.Format("Device connected! {0}", FormatDevice(displayName, modelName)));
    }

    public void DeckLinkDeviceRemoved(IDeckLink deckLinkDevice)
    {
      string displayName;
      string modelName;

      deckLinkDevice.GetDisplayName(out displayName);
      deckLinkDevice.GetModelName(out modelName);

      Run(() => notifications.Text = string.Format("Device disconnected! {0}", FormatDevice(displayName, modelName)));
    }

    private void find_Click(object sender, EventArgs e)
    {      
      // Create the COM instance
      IDeckLinkIterator deckLinkIterator = new CDeckLinkIterator();
      if (deckLinkIterator == null)
      {
        MessageBox.Show("Deck link drivers are not installed!", "Error");
        return;
      }
      
      // Get the first DeckLink card
      deckLinkIterator.Next(out _DeckLink);
      if (_DeckLink == null)
      {
        stream.Enabled = false;
        MessageBox.Show("No connected decklink device found", "Error");
        return;
      }

      string displayName;
      string modelName;

      _DeckLink.GetDisplayName(out displayName);
      _DeckLink.GetModelName(out modelName);

      stream.Enabled = true;
      this.deviceName.Text = string.Format("Device chosen: {0}", FormatDevice(displayName, modelName));
    }

    private void stream_Click(object x, EventArgs y)
    {
      // Get the IDeckLinkOutput interface
      var input = (IDeckLinkInput)_DeckLink;
      var output = (IDeckLinkOutput)_DeckLink;
            
      if (_Streaming)
      {
        KillStream();
      }
      else
      {
        IDeckLinkDisplayModeIterator displayIterator;
        input.GetDisplayModeIterator(out displayIterator);

        var supportedModes = new List<IDeckLinkDisplayMode>();
        
        input.SetCallback(this);
        input.SetScreenPreviewCallback(this);

        var flags = _BMDVideoInputFlags.bmdVideoInputFlagDefault | _BMDVideoInputFlags.bmdVideoInputEnableFormatDetection;
        var format = _BMDPixelFormat.bmdFormat8BitYUV;
        var display = _BMDDisplayMode.bmdModeHD1080i50;

        _BMDDisplayModeSupport support;
        IDeckLinkDisplayMode tmp;
        input.DoesSupportVideoMode(display, format, flags, out support, out tmp);

        if (support != _BMDDisplayModeSupport.bmdDisplayModeSupported)
          throw new Exception("display mode not working: " + support);

        if (writeRaw.Checked)
        {
          _VideoWriter = new BinaryWriter(File.Open(_RawVideoFile, FileMode.OpenOrCreate));
          _AudioWriter = new BinaryWriter(File.Open(_RawAudioFile, FileMode.OpenOrCreate));
        }

        if (writeEncoded.Checked)
        {
          _EncodedStream = new FileStream(_EncodedVideoFile, FileMode.Create, FileAccess.Write);

          _EncodeTask = _VideoConverter.ConvertLiveMedia(
            "rawvideo",
            _EncodedStream,
            "h264",
            new ConvertSettings()
            {
              CustomInputArgs = " -pix_fmt uyvy422 -video_size 1920x1080 -framerate 25",
            });

          _EncodeTask.Start();
        }

        input.EnableVideoInput(display, format, flags);
        input.EnableAudioInput(_AudioSampleRate, _AudioSampleType, _AudioChannels);
        
        input.StartStreams();

        stream.Text = "Kill";
        _Streaming = true;
      }
    }

    private void KillStream()
    {
      if (!_Streaming) return;

      _VideoLock.Wait();
      try
      {
        var input = (IDeckLinkInput)_DeckLink;

        input.StopStreams();
        input.DisableVideoInput();
        input.DisableAudioInput();

        stream.Text = "Stream";
        _Streaming = false;
        _FrameCount = 0;
        _PreviewCount = 0;
        frameCount.Text = string.Empty;
        previewCount.Text = string.Empty;
      }
      finally
      {
        _VideoLock.Release();
      }
    }

    private void burn_Click(object sender, EventArgs e)
    {
      // Destroy all the writers...
      if (_VideoWriter != null)
      {
        _VideoWriter.Close();
        _VideoWriter.Dispose();
        _VideoWriter = null;
      }

      if (_AudioWriter != null)
      {
        _AudioWriter.Close();
        _AudioWriter.Dispose();
        _AudioWriter = null;
      }

      if (_EncodeTask != null)
      {
        _EncodeTask.Stop();
        _EncodeTask = null;
      }

      if (_EncodedStream != null)
      {
        _EncodedStream.Close();
        _EncodedStream.Dispose();
        _EncodedStream = null;
      }

      // Make the audio a WAV
      var sampleData = File.ReadAllBytes(_RawAudioFile);

      using (var waveStream = new MemoryStream())
      {
        using (var bw = new BinaryWriter(waveStream))
        {
          var audioSampleRate = (int)_AudioSampleRate;

          bw.Write(new char[4] { 'R', 'I', 'F', 'F' });
          int fileSize = 36 + sampleData.Length;
          bw.Write(fileSize);
          bw.Write(new char[8] { 'W', 'A', 'V', 'E', 'f', 'm', 't', ' ' });
          bw.Write((int)16);
          bw.Write((short)1);
          bw.Write((short)_AudioChannels);
          bw.Write(audioSampleRate);
          bw.Write((int)(audioSampleRate * ((_AudioSampleDepth * _AudioChannels) / 8)));
          bw.Write((short)((_AudioSampleDepth * _AudioChannels) / 8));
          bw.Write((short)_AudioSampleDepth);

          bw.Write(new char[4] { 'd', 'a', 't', 'a' });
          bw.Write(sampleData.Length);

          bw.Write(sampleData, 0, sampleData.Length);

          waveStream.Position = 0;

          File.WriteAllBytes(_WavAudioFile, waveStream.ToArray());
        }
      }

      // Combine them
      _VideoConverter.ConvertMedia(_EncodedVideoFile, "h264", _FinalFile, null, new ConvertSettings()
      {
        CustomOutputArgs = " -i " + _WavAudioFile + " -vcodec libx264 -acodec libmp3lame",
      });
    }

    /// <summary>
    /// SetCallback
    /// </summary>
    public void VideoInputFormatChanged(_BMDVideoInputFormatChangedEvents notificationEvents, IDeckLinkDisplayMode newDisplayMode, _BMDDetectedVideoInputFormatFlags detectedSignalFlags)
    {
      if (newDisplayMode == null) return;

      Run(() => notifications.Text = string.Format("Video format changed: mode={0}", newDisplayMode.GetDisplayMode()));
      Marshal.ReleaseComObject(newDisplayMode);
    }

    public void VideoInputFrameArrived(IDeckLinkVideoInputFrame videoFrame, IDeckLinkAudioInputPacket audioPacket)
    {
      if (videoFrame == null && audioPacket == null) return;
      try
      {
        if (!_Streaming) return;
        if (!_VideoLock.Wait(TimeSpan.Zero)) return;
        try
        {
          Interlocked.Increment(ref _FrameCount);
          Run(() => frameCount.Text = _FrameCount.ToString());

          if (videoFrame != null)
          {
            var rowBytes = videoFrame.GetRowBytes();
            var height = videoFrame.GetHeight();

            IntPtr framePointer;
            videoFrame.GetBytes(out framePointer);

            var frame = new byte[rowBytes * height];
            Marshal.Copy(framePointer, frame, 0, frame.Length);

            if (writeRaw.Checked)
              _VideoWriter.Write(frame);

            if (writeEncoded.Checked)
              _EncodeTask.Write(frame, 0, frame.Length);
          }

          if (audioPacket != null)
          {
            IntPtr audioPointer;
            audioPacket.GetBytes(out audioPointer);

            var frameCount = audioPacket.GetSampleFrameCount();

            var audio = new byte[frameCount * _AudioChannels * (_AudioSampleDepth / 8)];
            Marshal.Copy(audioPointer, audio, 0, audio.Length);

            if (writeRaw.Checked)
              _AudioWriter.Write(audio);
          }
        }
        finally
        {
          _VideoLock.Release();
        }
      }
      finally
      {
        if (videoFrame != null) Marshal.ReleaseComObject(videoFrame);
        if (audioPacket != null) Marshal.ReleaseComObject(audioPacket);
      }
    }

    /// <summary>
    /// SetScreenPreviewCallback
    /// </summary>
    public void DrawFrame(IDeckLinkVideoFrame theFrame)
    {
      if (theFrame == null) return;
      try
      {
        if (!_Streaming) return;

        Interlocked.Increment(ref _PreviewCount);
        Run(() => previewCount.Text = _PreviewCount.ToString());

        _GLHelper.SetFrame(theFrame);

        previewBox.MakeCurrent();

        _GLHelper.PaintGL();

        previewBox.SwapBuffers();
        previewBox.Context.MakeCurrent(null);
      }
      finally
      {
        Marshal.ReleaseComObject(theFrame);
      }
    }
    
    private void SetupPreviewBox()
    {
      int w = previewBox.Width;
      int h = previewBox.Height;

      GL.MatrixMode(MatrixMode.Projection);
      GL.LoadIdentity();
      GL.Ortho(-1, 1, -1, 1, -1, 1); // Bottom-left corner pixel has coordinate (0, 0)
      GL.Viewport(0, 0, w, h); // Use all of the glControl painting area
      
      _GLHelper.InitializeGL();

      GL.ClearColor(Color.SkyBlue); // So we can see the box is actually workign
      GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
      previewBox.SwapBuffers();

      previewBox.Context.MakeCurrent(null);
    }
  }
}
