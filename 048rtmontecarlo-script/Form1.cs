﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Threading;
using System.Windows.Forms;
using GuiSupport;
using MathSupport;
using Utilities;

namespace Rendering
{
  public partial class Form1: Form, IRenderProgressForm
  {
    public static readonly string rev = Util.SetVersion ( "$Rev$" );

    public static Form1 singleton = null;

    /// <summary>
    /// Current output raster image. Locked access.
    /// </summary>
    internal Bitmap outputImage = null;

    /// <summary>
    /// Scenes for the listbox: sceneName -> {sceneDelegate | scriptFileName}
    /// </summary>
    public Dictionary<string, object> sceneRepository = null;

    public Scripts MyScenes;

    /// <summary>
    /// Index of the current (selected) scene.
    /// </summary>
    protected volatile int selectedScene = 0;

    /// <summary>
    /// If positive, new scene & image-function & renderer has to be created..
    /// </summary>
    protected bool dirty = true;

    /// <summary>
    /// Ray-based renderer in form of image function.
    /// For single sample computing only.
    /// </summary>
    protected IImageFunction imfs = null;

    /// <summary>
    /// Image width in pixels, 0 for default value (according to panel size).
    /// </summary>
    public int ImageWidth = 0;

    /// <summary>
    /// Image height in pixels, 0 for default value (according to panel size).
    /// </summary>
    public int ImageHeight = 0;

    /// <summary>
    /// Global instance of a random generator.
    /// </summary>
    public static RandomJames rnd = new RandomJames ();

    /// <summary>
    /// Global stopwatch for rendering thread. Locked access.
    /// </summary>
    public Stopwatch sw = new Stopwatch ();

    /// <summary>
    /// Rendering master thread.
    /// </summary>
    protected Thread aThread = null;

    /// <summary>
    /// Progress info / user break handling.
    /// </summary>
    protected RenderingProgress progress = null;

    protected string formTitle;

    private readonly PanAndZoomSupport panAndZoom;

    public Form1 ( string[] args )
    {
      singleton = this;
      InitializeComponent ();
      progress = new RenderingProgress ( this );

      // Init scenes etc.
      FormSupport.InitializeScenes ( args, out string name );

      Text += @" (" + rev + @") '" + name + '\'';
      formTitle = Text;
      SetWindowTitleSuffix ( " Zoom: 100%" );

      SetOptions ( args );
      buttonRes.Text = FormResolution.GetLabel ( ref ImageWidth, ref ImageHeight );

      Image image = null;

      try
      {
        image = Image.FromFile ( "Logo-CGG.png" );
      }
      catch ( Exception )
      {
        image = null;
      }

      if ( AdvancedTools.singleton == null )
        AdvancedTools.singleton = new AdvancedTools ( collectDataCheckBox, Notification );

      AdvancedTools.singleton.Initialize ();
      AdvancedTools.singleton.SetNewDimensions ( ImageWidth, ImageHeight ); //makes all maps to initialize again

      panAndZoom = new PanAndZoomSupport ( pictureBox1, image, SetWindowTitleSuffix );
    }

    /// <summary>
    /// Default behavior - create scene selected in the combo-box.
    /// Can handle InitSceneDelegate, InitSceneParamDelegate or CSscript file-name
    /// </summary>
    public IRayScene SceneByComboBox ()
    {
      string sceneName = (string) comboScene.Items [ selectedScene ];

      if ( sceneRepository.TryGetValue ( sceneName, out object definition ) )
        return Scripts.SceneFromObject ( sceneName, definition, textParam.Text, SetText );

      // fallback to a default scene;
      return Scenes.DefaultScene ();
    }

    public ComboBox ComboScene
    {
      get { return comboScene; }
    }

    public NumericUpDown NumericSupersampling
    {
      get { return numericSupersampling; }
    }

    public CheckBox CheckJitter
    {
      get { return checkJitter; }
    }

    public CheckBox CheckMultithreading
    {
      get { return checkMultithreading; }
    }

    public TextBox TextParam
    {
      get { return textParam; }
    }

    private void setImage ( ref Bitmap bakImage, Bitmap newImage )
    {
      panAndZoom.SetNewImage( newImage, false );

      //bakImage?.Dispose ();

      bakImage = newImage;
    }


    /// <summary>
    /// Worker-thread-specific data.
    /// </summary>
    protected class WorkerThreadInit
    {
      /// <summary>
      /// Worker id (for debugging purposes only).
      /// </summary>
      public int id;

      /// <summary>
      /// Worker-selector predicate.
      /// </summary>
      public ThreadSelector sel;

      public Bitmap image;

      /// <summary>
      /// Animated scene object or null if not animated.
      /// </summary>
      public ITimeDependent scene;

      /// <summary>
      /// Animated image function or null if not animated.
      /// </summary>
      public ITimeDependent imfunc;

      /// <summary>
      /// Actual rendering object.
      /// </summary>
      public IRenderer rend;

      public int width;
      public int height;

      public WorkerThreadInit ( IRenderer r, ITimeDependent sc, ITimeDependent imf, Bitmap im, int wid, int hei,
                                int threadNo, int threads )
      {
        scene  = sc;
        imfunc = imf;
        rend   = r;
        image  = im;
        id     = threadNo;
        width  = wid;
        height = hei;
        sel    = ( n ) => ( n % threads ) == threadNo;
      }
    }


    /// <summary>
    /// Routine of one worker-thread.
    /// Result image and rendering progress are the only two shared objects.
    /// </summary>
    /// <param name="spec">Thread-specific data (worker-thread-selector).</param>
    private void RenderWorker ( object spec )
    {
      if ( spec is WorkerThreadInit init )
      {
        MT.InitThreadData ();
        init.rend.RenderRectangle ( init.image, 0, 0, init.width, init.height,
                                    init.sel );
      }
    }

    private IImageFunction getImageFunction ( IRayScene sc, int width, int height )
    {
      IImageFunction imf = FormSupport.getImageFunction ( sc, TextParam.Text );
      imf.Width  = width;
      imf.Height = height;

      if ( imf is RayTracing rt )
      {
        rt.DoShadows     = checkShadows.Checked;
        rt.DoReflections = checkReflections.Checked;
        rt.DoRefractions = checkRefractions.Checked;
      }

      return imf;
    }

    private IRenderer getRenderer ( IImageFunction imf, int width, int height )
    {
      IRenderer rend = FormSupport.getRenderer ( imf, (int) numericSupersampling.Value, checkJitter.Checked ? 1.0 : 0.0,
                                                 TextParam.Text );
      rend.Width = width;
      rend.Height = height;
      rend.Adaptive = 8;
      rend.ProgressData = progress;

      return rend;
    }

    /// <summary>
    /// [Re]-renders the whole image (in separate thread).
    /// </summary>
    private void RenderImage ()
    {
      Cursor.Current = Cursors.WaitCursor;

      // determine output image size:
      int width = ImageWidth;
      if ( width <= 0 )
        width = panel1.Width;

      int height = ImageHeight;
      if ( height <= 0 )
        height = panel1.Height;

      Bitmap newImage = new Bitmap ( width, height, PixelFormat.Format24bppRgb );

      int threads = checkMultithreading.Checked ? Environment.ProcessorCount : 1;
      int t; // thread ordinal number

      WorkerThreadInit[] wti = new WorkerThreadInit[threads];

      // separate renderer, image function and the scene for each thread (safety precaution)
      for ( t = 0; t < threads; t++ )
      {
        IRayScene      sc  = FormSupport.getScene ();
        IImageFunction imf = getImageFunction ( sc, width, height );
        IRenderer      r   = getRenderer ( imf, width, height );
        wti[t] = new WorkerThreadInit ( r, sc as ITimeDependent, imf as ITimeDependent, newImage, width, height, t,
                                           threads );
      }

      progress.SyncInterval = ( ( width * (long) height ) > ( 2L << 20 ) ) ? 3000L : 1000L;
      progress.Reset ();
      CSGInnerNode.ResetStatistics ();

      lock ( sw )
        sw.Restart ();

      if ( threads > 1 )
      {
        Thread[] pool = new Thread[threads];
        for ( t = 0; t < threads; t++ )
          pool[t] = new Thread ( new ParameterizedThreadStart ( RenderWorker ) );
        for ( t = threads; --t >= 0; )
          pool[t].Start ( wti[t] );

        for ( t = 0; t < threads; t++ )
        {
          pool[t].Join ();
          pool[t] = null;
        }
      }
      else
      {
        MT.InitThreadData ();
        wti[0].rend.RenderRectangle ( newImage, 0, 0, width, height );
      }

      long elapsed;
      lock ( sw )
      {
        sw.Stop ();
        elapsed = sw.ElapsedMilliseconds;
      }

      string msg = string.Format ( CultureInfo.InvariantCulture,
                                   "{0:f1}s  [ {1}x{2}, mt{3}, r{4:#,#}k, i{5:#,#}k, bb{6:#,#}k, t{7:#,#}k ]",
                                   1.0e-3 * elapsed, width, height, threads,
                                   ( Intersection.countRays + 500L ) / 1000L,
                                   ( Intersection.countIntersections + 500L ) / 1000L,
                                   ( CSGInnerNode.countBoundingBoxes + 500L ) / 1000L,
                                   ( CSGInnerNode.countTriangles + 500L ) / 1000L );
      SetText ( msg );
      Console.WriteLine ( @"Rendering finished: " + msg );
      SetImage ( newImage );

      Cursor.Current = Cursors.Default;

      StopRendering ();
    }

    private void RenderImage2 ()
    {
      Cursor.Current = Cursors.WaitCursor;

      // determine output image size:
      int width = ImageWidth;
      if ( width <= 0 )
        width = panel1.Width;

      int height = ImageHeight;
      if ( height <= 0 )
        height = panel1.Height;

      Bitmap newImage = new Bitmap ( width, height, PixelFormat.Format24bppRgb );

      int threads = checkMultithreading.Checked ? Environment.ProcessorCount : 1;

      IRayScene      sc  = FormSupport.getScene ();
      IImageFunction imf = getImageFunction ( sc, width, height );
      IRenderer      r   = getRenderer ( imf, width, height );

      Master.singleton = new Master ( newImage, sc, r, RenderClientsForm.instance?.clients, threads, pointCloudCheckBox.Checked );
      Master.singleton.progressData = progress;
      Master.singleton.InitializeAssignments ( newImage, sc, r );

      if ( pointCloudCheckBox.Checked )
        Master.singleton.pointCloud.SetNecessaryFields ( PointCloudSavingStart, PointCloudSavingEnd, Notification, Invoke );

      progress.SyncInterval = ( ( width * (long) height ) > ( 2L << 20 ) ) ? 3000L : 1000L;
      progress.Reset ();
      CSGInnerNode.ResetStatistics ();

      lock ( sw )
        sw.Restart ();

      Master.singleton.StartThreads ();

      long elapsed;
      lock ( sw )
      {
        sw.Stop ();
        elapsed = sw.ElapsedMilliseconds;
      }

      string msg = string.Format ( CultureInfo.InvariantCulture,
                                   "{0:f1}s  [ {1}x{2}, mt{3}, r{4:#,#}k, i{5:#,#}k, bb{6:#,#}k, t{7:#,#}k ]",
                                   1.0e-3 * elapsed, width, height, threads,
                                   ( Intersection.countRays + 500L ) / 1000L,
                                   ( Intersection.countIntersections + 500L ) / 1000L,
                                   ( CSGInnerNode.countBoundingBoxes + 500L ) / 1000L,
                                   ( CSGInnerNode.countTriangles + 500L ) / 1000L );
      SetText ( msg );
      Console.WriteLine ( @"Rendering finished: " + msg );
      SetImage ( newImage );

      Cursor.Current = Cursors.Default;

      StopRendering ();
    }

    private void SetGUI ( bool enable )
    {
      numericSupersampling.Enabled =
      checkJitter.Enabled =
      checkShadows.Enabled =
      checkReflections.Enabled =
      checkReflections.Enabled =
      checkRefractions.Enabled =
      checkMultithreading.Enabled =
      buttonRender.Enabled =
      comboScene.Enabled =
      textParam.Enabled =
      buttonRes.Enabled =
      pointCloudCheckBox.Enabled =
      collectDataCheckBox.Enabled =
      savePointCloudButton.Enabled =
      buttonSave.Enabled = enable;

      buttonStop.Enabled = !enable;

      if ( MT.pointCloudSavingInProgress )
      {
        pointCloudCheckBox.Enabled = false;
        savePointCloudButton.Enabled = false;
      }        
    }


    delegate void SetImageCallback ( Bitmap newImage );


    public Stopwatch GetStopwatch ()
    {
      return sw;
    }

    public void SetImage ( Bitmap newImage )
    {
      if ( pictureBox1.InvokeRequired )
      {
        SetImageCallback si = new SetImageCallback ( SetImage );
        BeginInvoke ( si, new object[] { newImage } );
      }
      else
        setImage ( ref outputImage, newImage );
    }


    private delegate void SetTextCallback ( string text );


    public void SetText ( string text )
    {
      if ( labelElapsed.InvokeRequired )
      {
        SetTextCallback st = new SetTextCallback ( SetText );
        BeginInvoke ( st, new object[] { text } );
      }
      else
        labelElapsed.Text = text;
    }


    delegate void StopRenderingCallback ();

    /// <summary>
    /// Called to stop rendering and at the end of successful rendering 
    /// </summary>
    protected void StopRendering ()
    {
      if ( aThread == null )
        return;

      if ( buttonRender.InvokeRequired )
      {
        StopRenderingCallback ea = new StopRenderingCallback ( StopRendering );
        BeginInvoke ( ea );
      }
      else
      {
        // actually stop the rendering:
        lock ( progress )
          progress.Continue = false;
        aThread.Join ();
        aThread = null;

        // GUI stuff:
        SetGUI ( true );

        AdvancedToolsForm.singleton?.RenderButtonsEnabled ( true );
        AdvancedToolsForm.singleton?.ExportDataButtonsEnabled ( true );
        MT.renderingInProgress = false;
        MT.sceneRendered = true;

        if ( Master.singleton.pointCloud == null || Master.singleton.pointCloud.IsCloudEmpty )
          savePointCloudButton.Enabled = false;
        else if ( RayVisualizerForm.singleton != null )
          RayVisualizerForm.singleton.PointCloudButton.Enabled = true;
          

        AdvancedToolsForm.singleton?.NewImageRendered ();

        panAndZoom.SetNewImage ( panAndZoom.image, true );

        SetPreviousAndNextImageButtons ();        
      }
    }

    /// <summary>
    /// Shoots single primary ray only
    /// </summary>
    /// <param name="x">X-coordinate inside the raster image</param>
    /// <param name="y">Y-coordinate inside the raster image</param>
    private void singleSample ( int x, int y )
    {
      MT.singleRayTracing = true;
      RayVisualizer.singleton?.Reset ();

      // determine output image size:
      int width                 = ImageWidth;
      if ( width <= 0 ) width = panel1.Width;
      int height                = ImageHeight;
      if ( height <= 0 ) height = panel1.Height;

      if ( dirty || imfs == null )
      {
        imfs = getImageFunction ( FormSupport.getScene (), width, height );
        dirty = false;
      }

      double[] color = new double[3];
      long     hash  = imfs.GetSample ( x + 0.5, y + 0.5, color );
      labelSample.Text = string.Format ( CultureInfo.InvariantCulture,
                                         "Sample at [{0},{1}] = [{2:f},{3:f},{4:f}], {5:X}",
                                         x, y, color[0], color[1], color[2], hash );

      MT.singleRayTracing = false;
    }

    private void SetOptions ( string[] args )
    {
      foreach ( var opt in args )
        if ( !string.IsNullOrEmpty ( opt ) &&
             opt[0] == '-' &&
             opt.Contains ( "=" ) )
        {
          string[] opts = opt.Split ( '=' );
          if ( opts.Length > 1 )
            switch ( opts[0] )
            {
              case "jittering":
                checkJitter.Checked = Util.positive ( opts[1] );
                break;

              case "shadows":
                checkShadows.Checked = Util.positive ( opts[1] );
                break;

              case "reflections":
                checkReflections.Checked = Util.positive ( opts[1] );
                break;

              case "refractions":
                checkRefractions.Checked = Util.positive ( opts[1] );
                break;

              case "multi-threading":
                checkMultithreading.Checked = Util.positive ( opts[1] );
                break;

              case "supersampling":
              {
                if ( int.TryParse ( opts[1], out int v ) )
                  numericSupersampling.Text = v.ToString ();
              }
              break;
            }
        }
    }

    private void buttonRes_Click ( object sender, EventArgs e )
    {
      FormResolution form = new FormResolution ( ImageWidth, ImageHeight );
      if ( form.ShowDialog () == DialogResult.OK )
      {
        ImageWidth     = form.ImageWidth;
        ImageHeight    = form.ImageHeight;
        buttonRes.Text = FormResolution.GetLabel ( ref ImageWidth, ref ImageHeight );
      }
    }

    private void buttonRender_Click ( object sender, EventArgs e )
    {
      if ( collectDataCheckBox.Checked )
      {
        AdvancedTools.singleton.SetNewDimensions ( ImageWidth, ImageHeight );
        AdvancedTools.singleton.NewRenderInitialization ();
      }
      else if ( !AdvancedTools.singleton.mapsEmpty )
        AdvancedToolsForm.singleton?.ExportDataButtonsEnabled ( true );

      if ( aThread != null )
        return;

      // GUI stuff:
      SetGUI ( false );

      AdvancedToolsForm.singleton?.RenderButtonsEnabled ( false );
      MT.renderingInProgress = true;
      Statistics.Reset ();

      lock ( progress )
        progress.Continue = true;

      SetText ( "Wait a moment.." );
      aThread = new Thread ( new ThreadStart ( this.RenderImage2 ) );
      aThread.Start ();
    }

    private void buttonSave_Click ( object sender, EventArgs e )
    {
      if ( outputImage == null ||
           aThread != null ) return;

      SaveFileDialog sfd = new SaveFileDialog
      {
        Title = @"Save PNG file",
        Filter = @"PNG Files|*.png",
        AddExtension = true,
        FileName = "RenderResult"
      };
      if ( sfd.ShowDialog () != DialogResult.OK )
        return;

      outputImage.Save ( sfd.FileName, ImageFormat.Png );
    }

    private void buttonStop_Click ( object sender, EventArgs e )
    {
      StopRendering ();
    }

    private void comboScene_SelectedIndexChanged ( object sender, EventArgs e )
    {
      selectedScene = comboScene.SelectedIndex;
      dirty = true;
    }

    private void textParam_TextChanged ( object sender, EventArgs e )
    {
      dirty = true;
    }

    private void AdvancedToolsButton_Click ( object sender, EventArgs e )
    {
      if ( AdvancedToolsForm.singleton != null )
      {
        AdvancedToolsForm.singleton.Activate ();

        return; //only one instance of Form1 can exist at the time
      }

      AdvancedToolsForm advancedToolsForm = new AdvancedToolsForm ();
      advancedToolsForm.Show ();
    }

    private void RayVisualiserButton_Click ( object sender, EventArgs e )
    {
      if ( RayVisualizerForm.singleton != null )
      {
        RayVisualizerForm.singleton.Activate ();

        return; //only one instance of RayVisualizerForm can exist at the time
      }

      Cursor.Current = Cursors.WaitCursor;

      RayVisualizerForm rayVisualizerForm = new RayVisualizerForm ();
      rayVisualizerForm.Show ();
    }

    private void addRenderClientToolStripMenuItem_Click ( object sender, EventArgs e )
    {
      if ( RenderClientsForm.instance != null )
      {
        RenderClientsForm.instance.Show ();

        RenderClientsForm.instance.Activate ();

        return; //only one instance of renderClientsForm can exist at the time
      }

      RenderClientsForm renderClientsForm = new RenderClientsForm ();
      renderClientsForm.Show ();
    }

    /// <summary>
    /// Handles calling singleSample for RayVisualizer and picture box image pan control
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void pictureBox1_MouseDown ( object sender, MouseEventArgs e )
    {
      bool condition = aThread == null && e.Button == MouseButtons.Left && MT.sceneRendered && !MT.renderingInProgress;

      panAndZoom.MouseDownRegistration ( e, singleSample, condition, ModifierKeys, out Cursor cursor );

      if ( cursor != null )
        Cursor = cursor;     
    }

    /// <summary>
    /// Handles calling singleSample for RayVisualizer and picture box image pan control
    /// </summary>
    /// <param name="sender">Not needed</param>
    /// <param name="e">Needed for mouse button detection and cursor location</param>
    private void pictureBox1_MouseMove ( object sender, MouseEventArgs e )
    {
      bool condition = aThread == null && e.Button == MouseButtons.Left && MT.sceneRendered && !MT.renderingInProgress;

      panAndZoom.MouseMoveRegistration ( e, singleSample, condition, ModifierKeys, out Cursor cursor );

      if ( cursor != null )
        Cursor = cursor;
    }

    /// <summary>
    /// Called as MouseUp event from PictureBox
    /// </summary>
    /// <param name="sender">Not needed</param>
    /// <param name="e">Not needed</param>
    private void pictureBox1_MouseUp ( object sender, MouseEventArgs e )
    {
      panAndZoom.MouseUpRegistration ( out Cursor cursor );

      Cursor = cursor;
    }

    /// <summary>
    /// Catches mouse wheel movement for zoom in/out of image in picture box
    /// </summary>
    /// <param name="sender">Not needed</param>
    /// <param name="e">Needed for mouse wheel delta value and cursor location</param>
    private void pictureBox1_MouseWheel ( object sender, MouseEventArgs e )
    {
      panAndZoom.MouseWheelRegistration ( e, ModifierKeys );
    }

    /// <summary>
    /// Sets necessary stuff at form load
    /// </summary>
    /// <param name="sender">Not needed</param>
    /// <param name="e">Not needed</param>
    private void Form1_Load ( object sender, EventArgs e )
    {
      pictureBox1.SizeMode   =  PictureBoxSizeMode.Zoom;
      pictureBox1.MouseWheel += new MouseEventHandler ( pictureBox1_MouseWheel );
      KeyPreview             =  true;
    }

    private void Form1_FormClosing ( object sender, FormClosingEventArgs e )
    {
      StopRendering ();
    }

    /// <summary>
    /// Called when any key is pressed;
    /// Used for zoom using keys
    /// </summary>
    /// <param name="sender">Not needed</param>
    /// <param name="e">Needed to get pressed key</param>
    private void Form1_KeyDown ( object sender, KeyEventArgs e )
    {
      if ( e.KeyCode == Keys.R )
        panAndZoom.Reset ();

      panAndZoom.KeyDownRegistration ( e.KeyCode, ModifierKeys );
    }


    /// <summary>
    /// Called every time main picture box is needed to be re-painted
    /// Used for re-painting after request for zoom in/out or pan
    /// </summary>
    /// <param name="sender">Not needed</param>
    /// <param name="e">Needed to get Graphics class associated with PictureBox</param>
    private void pictureBox1_Paint ( object sender, PaintEventArgs e )
    {     
      panAndZoom.Paint ( e );
    }

    /// <summary>
    /// Opens SaveFileDialog and calls method SaveToPLYFile in PointCloud class
    /// </summary>
    /// <param name="sender">Not needed</param>
    /// <param name="e">Not needed</param>
    private void SavePointCloudButton_Click ( object sender, EventArgs e )
    {
      SaveFileDialog sfd = new SaveFileDialog
      {
        Title = @"Save PLY file",
        Filter = @"PLY Files|*.ply",
        AddExtension = true,
        FileName = "PointCloud"
      };
      if ( sfd.ShowDialog () != DialogResult.OK )
        return;

      AdvancedTools.singleton?.pointCloud?.SaveToPLYFile ( sfd.FileName );
    }

    /// <summary>
    /// Displays bubble notification (in system tray or Notification Center) with desired title, text and diration
    /// </summary>
    /// <param name="title">Title of notification</param>
    /// <param name="text">Body of notification</param>
    /// <param name="duration">Duration of notification in milliseconds</param>
    public void Notification ( string title, string text, int duration )
    {
      if ( text == null || title == null )
        return;

      notificationIcon.Icon = SystemIcons.Information;

      notificationIcon.Visible         = true;
      notificationIcon.BalloonTipTitle = title;
      notificationIcon.BalloonTipText  = text;
      notificationIcon.ShowBalloonTip ( duration );
      notificationIcon.Visible = false;
    }

    /// <summary>
    /// Adds suffix to default text in Form>text property (title text in upper panel, between icon and minimize and close buttons)
    /// </summary>
    /// <param name="suffix">Suffix to add to constant Form title</param>
    private void SetWindowTitleSuffix ( string suffix )
    {
      if ( string.IsNullOrEmpty ( suffix ) )
        Text = formTitle;
      else
        Text = formTitle + ' ' + suffix;
    }

    /// <summary>
    /// Resets image in picture box to 100% zoom and default position
    /// (left upper corner of image in left upper conrner of picture box)
    /// </summary>
    /// <param name="sender">Not needed</param>
    /// <param name="e">Not needed</param>
    private void ResetButton_Click ( object sender, EventArgs e )
    {
      panAndZoom.Reset ();
    }

    /// <summary>
    /// Disables GUI elements related to point cloud (for example during saving file)
    /// </summary>
    public void PointCloudSavingStart ()
    {
      singleton.pointCloudCheckBox.Enabled = false;
      singleton.savePointCloudButton.Enabled = false;
    }

    /// <summary>
    /// Enables GUI elements related to point cloud (for example after file is saved)
    /// </summary>
    public void PointCloudSavingEnd ()
    {
      singleton.pointCloudCheckBox.Enabled = true;
      singleton.savePointCloudButton.Enabled = true;
    }

    private void pointCloudCheckBox_CheckedChanged ( object sender, EventArgs e )
    {
      MT.pointCloudCheckBox = pointCloudCheckBox.Checked;
    }

    private void PreviousImageButton_Click ( object sender, EventArgs e )
    {
      panAndZoom.SetPreviousImageFromHistory ();

      SetPreviousAndNextImageButtons ();
    }  

    private void NextImageButton_Click ( object sender, EventArgs e )
    {
      panAndZoom.SetNextImageFromHistory ();

      SetPreviousAndNextImageButtons ();
    }

    private void SetPreviousAndNextImageButtons ()
    {
      NextImageButton.Enabled = panAndZoom.NextImageAvailable ();
      PreviousImageButton.Enabled = panAndZoom.PreviousImageAvailable ();
    }
  }
}
