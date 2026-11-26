using System.Drawing;
using System.Numerics;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct2D;
using Windows.Win32.Graphics.Direct2D.Common;
using Windows.Win32.Graphics.DirectWrite;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.Graphics.Imaging;
using Windows.Win32.System.Com;
using Windows.Win32.UI.WindowsAndMessaging;

namespace SpotDock;

internal sealed unsafe class LayeredSurface : IDisposable
{
    private HDC _memDc;
    private HBITMAP _bmp;
    private HGDIOBJ _oldBmp;
    private int _widthPx;
    private int _heightPx;

    public HDC MemDc => _memDc;
    public int WidthPx => _widthPx;
    public int HeightPx => _heightPx;

    public bool EnsureSize(int widthPx, int heightPx)
    {
        if (widthPx <= 0 || heightPx <= 0) return false;
        if (_memDc != HDC.Null && _bmp != HBITMAP.Null && _widthPx == widthPx && _heightPx == heightPx)
            return true;

        ReleaseBitmap();
        if (_memDc == HDC.Null)
        {
            _memDc = PInvoke.CreateCompatibleDC(HDC.Null);
            if (_memDc == HDC.Null) return false;
        }

        var bmi = new BITMAPINFO();
        bmi.bmiHeader.biSize = (uint)sizeof(BITMAPINFOHEADER);
        bmi.bmiHeader.biWidth = widthPx;
        bmi.bmiHeader.biHeight = -heightPx; // top-down
        bmi.bmiHeader.biPlanes = 1;
        bmi.bmiHeader.biBitCount = 32;
        bmi.bmiHeader.biCompression = 0; // BI_RGB

        void* bits = null;
        _bmp = PInvoke.CreateDIBSection(_memDc, &bmi, DIB_USAGE.DIB_RGB_COLORS, &bits, HANDLE.Null, 0);
        if (_bmp == HBITMAP.Null)
        {
            _widthPx = 0;
            _heightPx = 0;
            return false;
        }

        _oldBmp = PInvoke.SelectObject(_memDc, (HGDIOBJ)_bmp.Value);
        _widthPx = widthPx;
        _heightPx = heightPx;
        return true;
    }

    public void Present(HWND hwnd, Point* ptDst = null)
    {
        if (_memDc == HDC.Null || _widthPx <= 0 || _heightPx <= 0) return;
        var size = new SIZE { cx = _widthPx, cy = _heightPx };
        var ptSrc = default(Point);
        var blend = new BLENDFUNCTION
        {
            BlendOp = 0, // AC_SRC_OVER
            BlendFlags = 0,
            SourceConstantAlpha = 255,
            AlphaFormat = 1, // AC_SRC_ALPHA
        };
        PInvoke.UpdateLayeredWindow(hwnd, HDC.Null, ptDst, &size, _memDc, &ptSrc, default, &blend,
            UPDATE_LAYERED_WINDOW_FLAGS.ULW_ALPHA);
    }

    private void ReleaseBitmap()
    {
        if (_bmp != HBITMAP.Null)
        {
            if (_memDc != HDC.Null && _oldBmp != HGDIOBJ.Null)
                PInvoke.SelectObject(_memDc, _oldBmp);
            PInvoke.DeleteObject((HGDIOBJ)_bmp.Value);
            _bmp = HBITMAP.Null;
            _oldBmp = HGDIOBJ.Null;
        }
        _widthPx = 0;
        _heightPx = 0;
    }

    public void Dispose()
    {
        ReleaseBitmap();
        if (_memDc != HDC.Null)
        {
            PInvoke.DeleteDC(_memDc);
            _memDc = HDC.Null;
        }
    }
}

internal struct WidgetRenderState
{
    public double DpiScale;
    public int WidthPx;
    public int HeightPx;
    public double WidthDip;
    public double HeightDip;
    public bool LightTheme;
    public float WidgetOpacity;
    public bool ContentVisible;
    public bool ShowArt;
    public bool ShowProgress;
    public double TextWidthDip;
    public bool ShowLike;
    public bool ShowPrev;
    public bool ShowNext;
    public string Title;
    public string Artist;
    public float MarqueeOffsetX;
    public float MarqueeDistance;
    public bool MarqueeShowSecond;
    public IntPtr ArtBase;
    public IntPtr ArtTop;
    public float ArtBaseOpacity;
    public float ArtTopOpacity;
    public bool IsPlaying;
    public bool? Liked;
    public float LikeScale;
    public float NextScale;
    public float LikeHover;
    public float PrevHover;
    public float PlayHover;
    public float NextHover;
    public bool LikePressed;
    public bool PrevPressed;
    public bool PlayPressed;
    public bool NextPressed;
    public double ProgressFraction;
    public double ProgressHoverFraction;
    public bool ProgressHovered;
    public bool Scrubbing;
    public double ProgressHeightDip;
    public double ProgressRadiusDip;
}

internal sealed unsafe class D2DRenderer : IDisposable
{
    public static D2DRenderer Shared { get; } = new();

    private ID2D1Factory* _d2dFactory;
    private ID2D1DCRenderTarget* _dcTarget;
    private ID2D1SolidColorBrush* _brush;
    private ID2D1StrokeStyle* _roundStroke;
    private ID2D1Layer* _clipLayer;

    private ID2D1PathGeometry* _playGeo;
    private ID2D1PathGeometry* _pauseGeo;
    private ID2D1PathGeometry* _prevGeo;
    private ID2D1PathGeometry* _nextGeo;
    private ID2D1PathGeometry* _addCircleGeo;
    private ID2D1PathGeometry* _checkCircleGeo;

    private IDWriteFactory* _dwriteFactory;
    private IDWriteTextFormat* _titleEllipsisFormat;
    private IDWriteTextFormat* _titleNoTrimFormat;
    private IDWriteTextFormat* _artistFormat;
    private IDWriteTextFormat* _placeholderFormat;
    private IDWriteTextFormat* _pillPosFormat;
    private IDWriteTextFormat* _pillDurFormat;
    private IDWriteTypography* _tabularTypography;

    private bool _initialized;

    private static D2D_MATRIX_3X2_F ToD2D(Matrix3x2 m) => new()
    {
        Anonymous = new D2D_MATRIX_3X2_F._Anonymous_e__Union
        {
            Anonymous1 = new D2D_MATRIX_3X2_F._Anonymous_e__Union._Anonymous1_e__Struct
            {
                m11 = m.M11, m12 = m.M12,
                m21 = m.M21, m22 = m.M22,
                dx = m.M31, dy = m.M32,
            }
        }
    };

    private static readonly D2D_MATRIX_3X2_F IdentityMatrix = ToD2D(Matrix3x2.Identity);

    public bool Initialize()
    {
        if (_initialized) return true;
        try
        {
            Guid d2dGuid = typeof(ID2D1Factory).GUID;
            ID2D1Factory* d2dFactory = null;
            if (PInvoke.D2D1CreateFactory(
                    D2D1_FACTORY_TYPE.D2D1_FACTORY_TYPE_SINGLE_THREADED,
                    &d2dGuid, null, (void**)&d2dFactory).Failed || d2dFactory == null)
                return false;
            _d2dFactory = d2dFactory;

            var rtProps = new D2D1_RENDER_TARGET_PROPERTIES
            {
                type = D2D1_RENDER_TARGET_TYPE.D2D1_RENDER_TARGET_TYPE_SOFTWARE,
                pixelFormat = new D2D1_PIXEL_FORMAT
                {
                    format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                    alphaMode = D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_PREMULTIPLIED,
                },
                dpiX = 96,
                dpiY = 96,
            };
            ID2D1DCRenderTarget* dcTarget = null;
            _d2dFactory->CreateDCRenderTarget(&rtProps, &dcTarget);
            if (dcTarget == null) return false;
            _dcTarget = dcTarget;

            var black = new D2D1_COLOR_F { r = 0, g = 0, b = 0, a = 1 };
            ID2D1SolidColorBrush* brush = null;
            _dcTarget->CreateSolidColorBrush(&black, null, &brush);
            _brush = brush;

            ID2D1Layer* clipLayer = null;
            _dcTarget->CreateLayer(null, &clipLayer);
            _clipLayer = clipLayer;

            var strokeProps = new D2D1_STROKE_STYLE_PROPERTIES
            {
                startCap = D2D1_CAP_STYLE.D2D1_CAP_STYLE_ROUND,
                endCap = D2D1_CAP_STYLE.D2D1_CAP_STYLE_ROUND,
                dashCap = D2D1_CAP_STYLE.D2D1_CAP_STYLE_ROUND,
                lineJoin = D2D1_LINE_JOIN.D2D1_LINE_JOIN_ROUND,
                miterLimit = 10f,
            };
            ID2D1StrokeStyle* roundStroke = null;
            _d2dFactory->CreateStrokeStyle(&strokeProps, null, 0, &roundStroke);
            _roundStroke = roundStroke;

            BuildGeometries();

            Guid dwGuid = typeof(IDWriteFactory).GUID;
            IDWriteFactory* dwriteFactory = null;
            if (PInvoke.DWriteCreateFactory(
                    DWRITE_FACTORY_TYPE.DWRITE_FACTORY_TYPE_SHARED,
                    &dwGuid, (void**)&dwriteFactory).Failed || dwriteFactory == null)
                return false;
            _dwriteFactory = dwriteFactory;

            BuildTextFormats();

            _initialized = _brush != null && _titleEllipsisFormat != null;
            return _initialized;
        }
        catch
        {
            return false;
        }
    }

    private void BuildGeometries()
    {
        _playGeo = BuildPath(sink =>
        {
            sink->BeginFigure(new D2D_POINT_2F { x = 6, y = 4 }, D2D1_FIGURE_BEGIN.D2D1_FIGURE_BEGIN_FILLED);
            sink->AddLine(new D2D_POINT_2F { x = 20, y = 12 });
            sink->AddLine(new D2D_POINT_2F { x = 6, y = 20 });
            sink->EndFigure(D2D1_FIGURE_END.D2D1_FIGURE_END_CLOSED);
        });

        _pauseGeo = BuildPath(sink =>
        {
            AddRectFigure(sink, 4.5f, 4f, 9.5f, 20f);
            AddRectFigure(sink, 14.5f, 4f, 19.5f, 20f);
        });

        _prevGeo = BuildPath(sink =>
        {
            sink->BeginFigure(new D2D_POINT_2F { x = 5, y = 5 }, D2D1_FIGURE_BEGIN.D2D1_FIGURE_BEGIN_HOLLOW);
            sink->AddLine(new D2D_POINT_2F { x = 5, y = 19 });
            sink->EndFigure(D2D1_FIGURE_END.D2D1_FIGURE_END_OPEN);

            sink->BeginFigure(new D2D_POINT_2F { x = 19, y = 4 }, D2D1_FIGURE_BEGIN.D2D1_FIGURE_BEGIN_FILLED);
            sink->AddLine(new D2D_POINT_2F { x = 9, y = 12 });
            sink->AddLine(new D2D_POINT_2F { x = 19, y = 20 });
            sink->EndFigure(D2D1_FIGURE_END.D2D1_FIGURE_END_CLOSED);
        });

        _nextGeo = BuildPath(sink =>
        {
            sink->BeginFigure(new D2D_POINT_2F { x = 19, y = 5 }, D2D1_FIGURE_BEGIN.D2D1_FIGURE_BEGIN_HOLLOW);
            sink->AddLine(new D2D_POINT_2F { x = 19, y = 19 });
            sink->EndFigure(D2D1_FIGURE_END.D2D1_FIGURE_END_OPEN);

            sink->BeginFigure(new D2D_POINT_2F { x = 5, y = 4 }, D2D1_FIGURE_BEGIN.D2D1_FIGURE_BEGIN_FILLED);
            sink->AddLine(new D2D_POINT_2F { x = 15, y = 12 });
            sink->AddLine(new D2D_POINT_2F { x = 5, y = 20 });
            sink->EndFigure(D2D1_FIGURE_END.D2D1_FIGURE_END_CLOSED);
        });

        _addCircleGeo = BuildPath(sink =>
        {
            sink->SetFillMode(D2D1_FILL_MODE.D2D1_FILL_MODE_ALTERNATE);
            AddCircleFigure(sink, 8f, 8f, 8f);
            AddCircleFigure(sink, 8f, 8f, 6.65f);
            AddRectFigure(sink, 7.325f, 4.25f, 8.675f, 11.75f);
            AddRectFigure(sink, 4.25f, 7.325f, 11.75f, 8.675f);
        });

        _checkCircleGeo = BuildPath(sink =>
        {
            sink->SetFillMode(D2D1_FILL_MODE.D2D1_FILL_MODE_ALTERNATE);
            AddCircleFigure(sink, 8f, 8f, 8f);

            sink->BeginFigure(new D2D_POINT_2F { x = 11.75f, y = 6.03f }, D2D1_FIGURE_BEGIN.D2D1_FIGURE_BEGIN_FILLED);
            sink->AddLine(new D2D_POINT_2F { x = 6.22f, y = 11.53f });
            sink->AddLine(new D2D_POINT_2F { x = 3.75f, y = 9.06f });
            sink->AddLine(new D2D_POINT_2F { x = 4.81f, y = 8.00f });
            sink->AddLine(new D2D_POINT_2F { x = 6.22f, y = 9.41f });
            sink->AddLine(new D2D_POINT_2F { x = 10.69f, y = 4.97f });
            sink->EndFigure(D2D1_FIGURE_END.D2D1_FIGURE_END_CLOSED);
        });
    }

    private delegate void SinkBuilder(ID2D1GeometrySink* sink);

    private ID2D1PathGeometry* BuildPath(SinkBuilder build)
    {
        ID2D1PathGeometry* geo = null;
        _d2dFactory->CreatePathGeometry(&geo);
        if (geo == null) return null;
        ID2D1GeometrySink* sink = null;
        geo->Open(&sink);
        if (sink != null)
        {
            build(sink);
            sink->Close();
            sink->Release();
        }
        return geo;
    }

    private static void AddRectFigure(ID2D1GeometrySink* sink, float l, float t, float r, float b)
    {
        sink->BeginFigure(new D2D_POINT_2F { x = l, y = t }, D2D1_FIGURE_BEGIN.D2D1_FIGURE_BEGIN_FILLED);
        sink->AddLine(new D2D_POINT_2F { x = r, y = t });
        sink->AddLine(new D2D_POINT_2F { x = r, y = b });
        sink->AddLine(new D2D_POINT_2F { x = l, y = b });
        sink->EndFigure(D2D1_FIGURE_END.D2D1_FIGURE_END_CLOSED);
    }

    private static void AddCircleFigure(ID2D1GeometrySink* sink, float cx, float cy, float r)
    {
        sink->BeginFigure(new D2D_POINT_2F { x = cx - r, y = cy }, D2D1_FIGURE_BEGIN.D2D1_FIGURE_BEGIN_FILLED);
        var sz = new D2D_SIZE_F { width = r, height = r };
        sink->AddArc(new D2D1_ARC_SEGMENT
        {
            point = new D2D_POINT_2F { x = cx + r, y = cy },
            size = sz,
            rotationAngle = 0,
            sweepDirection = D2D1_SWEEP_DIRECTION.D2D1_SWEEP_DIRECTION_CLOCKWISE,
            arcSize = D2D1_ARC_SIZE.D2D1_ARC_SIZE_SMALL,
        });
        sink->AddArc(new D2D1_ARC_SEGMENT
        {
            point = new D2D_POINT_2F { x = cx - r, y = cy },
            size = sz,
            rotationAngle = 0,
            sweepDirection = D2D1_SWEEP_DIRECTION.D2D1_SWEEP_DIRECTION_CLOCKWISE,
            arcSize = D2D1_ARC_SIZE.D2D1_ARC_SIZE_SMALL,
        });
        sink->EndFigure(D2D1_FIGURE_END.D2D1_FIGURE_END_CLOSED);
    }

    private void BuildTextFormats()
    {
        _titleEllipsisFormat = CreateFormat("Segoe UI", DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_SEMI_BOLD, 12f, ellipsis: true);
        _titleNoTrimFormat = CreateFormat("Segoe UI", DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_SEMI_BOLD, 12f, ellipsis: false);
        _artistFormat = CreateFormat("Segoe UI", DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_NORMAL, 11f, ellipsis: true);
        _placeholderFormat = CreateFormat("Segoe Fluent Icons", DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_NORMAL, 14f, ellipsis: false);
        if (_placeholderFormat != null)
        {
            _placeholderFormat->SetTextAlignment(DWRITE_TEXT_ALIGNMENT.DWRITE_TEXT_ALIGNMENT_CENTER);
            _placeholderFormat->SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT.DWRITE_PARAGRAPH_ALIGNMENT_CENTER);
        }
        _pillPosFormat = CreateFormat("Segoe UI", DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_SEMI_BOLD, 11.5f, ellipsis: false);
        _pillDurFormat = CreateFormat("Segoe UI", DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_NORMAL, 11.5f, ellipsis: false);

        IDWriteTypography* typo = null;
        _dwriteFactory->CreateTypography(&typo);
        if (typo != null)
        {
            typo->AddFontFeature(new DWRITE_FONT_FEATURE
            {
                nameTag = DWRITE_FONT_FEATURE_TAG.DWRITE_FONT_FEATURE_TAG_TABULAR_FIGURES,
                parameter = 1,
            });
            _tabularTypography = typo;
        }
    }

    private IDWriteTextFormat* CreateFormat(string family, DWRITE_FONT_WEIGHT weight, float sizeDip, bool ellipsis)
    {
        IDWriteTextFormat* fmt = null;
        fixed (char* pFam = family)
        fixed (char* pLoc = "en-US")
        {
            _dwriteFactory->CreateTextFormat(
                pFam, null, weight, DWRITE_FONT_STYLE.DWRITE_FONT_STYLE_NORMAL,
                DWRITE_FONT_STRETCH.DWRITE_FONT_STRETCH_NORMAL, sizeDip, pLoc, &fmt);
        }
        if (fmt == null) return null;
        fmt->SetWordWrapping(DWRITE_WORD_WRAPPING.DWRITE_WORD_WRAPPING_NO_WRAP);
        if (ellipsis)
        {
            IDWriteInlineObject* sign = null;
            _dwriteFactory->CreateEllipsisTrimmingSign(fmt, &sign);
            if (sign != null)
            {
                var trim = new DWRITE_TRIMMING
                {
                    granularity = DWRITE_TRIMMING_GRANULARITY.DWRITE_TRIMMING_GRANULARITY_CHARACTER,
                };
                fmt->SetTrimming(&trim, sign);
                sign->Release();
            }
        }
        return fmt;
    }

    public double MeasureTitleWidth(string text)
    {
        if (!_initialized || _dwriteFactory == null || _titleNoTrimFormat == null || string.IsNullOrEmpty(text))
            return 0;

        IDWriteTextLayout* layout = null;
        try
        {
            fixed (char* pText = text)
                _dwriteFactory->CreateTextLayout(pText, (uint)text.Length, _titleNoTrimFormat, 10000f, 100f, &layout);
            if (layout == null) return 0;
            DWRITE_TEXT_METRICS metrics = default;
            layout->GetMetrics(&metrics);
            return Math.Ceiling(metrics.widthIncludingTrailingWhitespace);
        }
        catch
        {
            return 0;
        }
        finally
        {
            if (layout != null) layout->Release();
        }
    }

    public static (uint Width, uint Height, byte[] Pixels)? DecodeArtPixels(byte[] bytes, double dpiScale)
    {
        if (bytes.Length == 0)
            return null;

        NativeUia.EnsureComInitialized();
        int targetPx = Math.Clamp((int)Math.Ceiling(34 * Math.Max(1.0, dpiScale) * 1.5), 64, 128);
        IWICImagingFactory* wicFactory = null;
        IWICStream* stream = null;
        IWICBitmapDecoder* decoder = null;
        IWICBitmapFrameDecode* frame = null;
        IWICBitmapScaler* scaler = null;
        IWICFormatConverter* converter = null;

        try
        {
            Guid wicClsid = PInvoke.CLSID_WICImagingFactory;
            Guid wicIid = typeof(IWICImagingFactory).GUID;
            PInvoke.CoCreateInstance(&wicClsid, null, CLSCTX.CLSCTX_INPROC_SERVER, &wicIid, (void**)&wicFactory);
            if (wicFactory == null) return null;

            wicFactory->CreateStream(&stream);
            if (stream == null) return null;
            fixed (byte* pBytes = bytes)
                stream->InitializeFromMemory(pBytes, (uint)bytes.Length);

            decoder = wicFactory->CreateDecoderFromStream(
                (IStream*)stream, null, WICDecodeOptions.WICDecodeMetadataCacheOnDemand);
            if (decoder == null) return null;

            decoder->GetFrame(0, &frame);
            if (frame == null) return null;

            uint pw = 0, ph = 0;
            frame->GetSize(&pw, &ph);
            if (pw == 0 || ph == 0) return null;

            uint dstW = pw, dstH = ph;
            IWICBitmapSource* source = (IWICBitmapSource*)frame;
            if (pw >= ph && ph > (uint)targetPx)
            {
                dstH = (uint)targetPx;
                dstW = Math.Max(1u, (uint)Math.Round((double)pw * targetPx / ph));
            }
            else if (ph > pw && pw > (uint)targetPx)
            {
                dstW = (uint)targetPx;
                dstH = Math.Max(1u, (uint)Math.Round((double)ph * targetPx / pw));
            }

            if (dstW != pw || dstH != ph)
            {
                wicFactory->CreateBitmapScaler(&scaler);
                if (scaler != null)
                {
                    scaler->Initialize((IWICBitmapSource*)frame, dstW, dstH,
                        WICBitmapInterpolationMode.WICBitmapInterpolationModeFant);
                    source = (IWICBitmapSource*)scaler;
                }
            }

            wicFactory->CreateFormatConverter(&converter);
            if (converter == null) return null;

            Guid fmt = PInvoke.GUID_WICPixelFormat32bppPBGRA;
            converter->Initialize(source, &fmt, WICBitmapDitherType.WICBitmapDitherTypeNone,
                null, 0, WICBitmapPaletteType.WICBitmapPaletteTypeCustom);

            uint stride = dstW * 4;
            byte[] pixels = new byte[stride * dstH];
            fixed (byte* pPix = pixels)
                converter->CopyPixels(null, stride, (uint)pixels.Length, pPix);
            return (dstW, dstH, pixels);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (converter != null) converter->Release();
            if (scaler != null) scaler->Release();
            if (frame != null) frame->Release();
            if (decoder != null) decoder->Release();
            if (stream != null) stream->Release();
            if (wicFactory != null) wicFactory->Release();
        }
    }

    public IntPtr CreateArtBitmap(uint width, uint height, byte[] pixels)
    {
        if (!UiThread.IsOnUiThread)
        {
            Diag.Once("d2d-thread-art", "CreateArtBitmap called off the UI thread.");
            return IntPtr.Zero;
        }
        if (!_initialized || _dcTarget == null || width == 0 || height == 0 || pixels.Length < width * height * 4)
            return IntPtr.Zero;

        uint stride = width * 4;
        var bmpProps = new D2D1_BITMAP_PROPERTIES
        {
            pixelFormat = new D2D1_PIXEL_FORMAT
            {
                format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                alphaMode = D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_PREMULTIPLIED,
            },
            dpiX = 96f,
            dpiY = 96f,
        };
        fixed (byte* pPix = pixels)
        {
            ID2D1Bitmap* d2dBmp = null;
            _dcTarget->CreateBitmap(new D2D_SIZE_U { width = width, height = height },
                pPix, stride, &bmpProps, &d2dBmp);
            return (IntPtr)d2dBmp;
        }
    }

    public static void ReleaseBitmap(IntPtr bmp)
    {
        if (bmp != IntPtr.Zero)
            ((ID2D1Bitmap*)bmp)->Release();
    }

    public void RenderWidget(LayeredSurface surface, HWND hwnd, in WidgetRenderState s)
    {
        if (!UiThread.IsOnUiThread)
        {
            Diag.Once("d2d-thread-render", "RenderWidget called off the UI thread.");
            return;
        }
        if (!_initialized || _dcTarget == null || _brush == null) return;
        if (!surface.EnsureSize(s.WidthPx, s.HeightPx)) return;

        bool drawing = false;
        try
        {
            var rc = new RECT { left = 0, top = 0, right = s.WidthPx, bottom = s.HeightPx };
            _dcTarget->BindDC(surface.MemDc, &rc);

            float dpi = (float)(96.0 * s.DpiScale);
            _dcTarget->SetDpi(dpi, dpi);
            _dcTarget->SetTextAntialiasMode(D2D1_TEXT_ANTIALIAS_MODE.D2D1_TEXT_ANTIALIAS_MODE_GRAYSCALE);

            _dcTarget->BeginDraw();
            drawing = true;
            var clear = new D2D1_COLOR_F { r = 0, g = 0, b = 0, a = 0 };
            _dcTarget->Clear(&clear);
            var id = IdentityMatrix;
            _dcTarget->SetTransform(&id);

            // #01000000 hit-test background (unscaled by WidgetOpacity so mouse capture always works)
            SetBrushColor(0x01000000);
            var rootRect = new D2D1_ROUNDED_RECT
            {
                rect = new D2D_RECT_F { left = 0, top = 0, right = (float)s.WidthDip, bottom = (float)s.HeightDip },
                radiusX = 8f,
                radiusY = 8f,
            };
            _dcTarget->FillRoundedRectangle(&rootRect, (ID2D1Brush*)_brush);

            if (s.ContentVisible)
            {
                float op = s.WidgetOpacity;
                float x = 1f;
                const float yTop = 2f;

                if (s.ShowArt)
                {
                    DrawAlbumArt(x, yTop, in s, op);
                    x += 34f + 9f;
                }

                DrawTextStack(x, yTop, in s, op);
                x += (float)s.TextWidthDip + 3f;

                if (s.ShowLike)
                {
                    DrawIconButton(x, yTop, s.LikeHover, s.LikePressed, s.LightTheme, op);
                    uint likeColor = s.Liked == true
                        ? 0xFF1ED760u
                        : (s.Liked == false ? (s.LightTheme ? 0xFF000000u : 0xFFFFFFFFu) : (s.LightTheme ? 0x66000000u : 0x66FFFFFFu));
                    DrawVectorIcon(
                        s.Liked == true ? _checkCircleGeo : _addCircleGeo,
                        16f, 12.5f, x + 12.5f, yTop + 17f, s.LikeScale,
                        filled: true, stroked: false, likeColor, op);
                    x += 27f;
                }

                if (s.ShowPrev)
                {
                    DrawIconButton(x, yTop, s.PrevHover, s.PrevPressed, s.LightTheme, op);
                    uint fg = s.LightTheme ? 0xFF000000u : 0xFFFFFFFFu;
                    DrawVectorIcon(_prevGeo, 24f, 12f * (24f / 16f), x + 12.5f, yTop + 17f, 1f,
                        filled: false, stroked: true, fg, op * (s.PrevPressed ? 0.72f : 1f));
                    x += 27f;
                }

                {
                    DrawIconButton(x, yTop, s.PlayHover, s.PlayPressed, s.LightTheme, op);
                    uint fg = s.LightTheme ? 0xFF000000u : 0xFFFFFFFFu;
                    float cx = x + 12.5f;
                    DrawVectorIcon(s.IsPlaying ? _pauseGeo : _playGeo, 24f, 12f * (24f / 16f), cx, yTop + 17f, 1f,
                        filled: false, stroked: true, fg, op * (s.PlayPressed ? 0.72f : 1f));
                    x += 25f;
                }

                if (s.ShowNext)
                {
                    x += 2f;
                    DrawIconButton(x, yTop, s.NextHover, s.NextPressed, s.LightTheme, op);
                    uint fg = s.LightTheme ? 0xFF000000u : 0xFFFFFFFFu;
                    DrawVectorIcon(_nextGeo, 24f, 12f * (24f / 16f), x + 12.5f, yTop + 17f, s.NextScale,
                        filled: false, stroked: true, fg, op * (s.NextPressed ? 0.72f : 1f));
                }

                if (s.ShowProgress)
                    DrawProgressBar(yTop + 34f, in s, op);
            }

            _dcTarget->EndDraw();
            drawing = false;
            surface.Present(hwnd);
        }
        catch (Exception ex)
        {
            Diag.Log("RenderWidget error: " + ex);
        }
        finally
        {
            if (drawing)
            {
                try { _dcTarget->EndDraw(); } catch { }
            }
        }
    }

    private void DrawAlbumArt(float x, float y, in WidgetRenderState s, float op)
    {
        var rr = new D2D1_ROUNDED_RECT
        {
            rect = new D2D_RECT_F { left = x, top = y, right = x + 34f, bottom = y + 34f },
            radiusX = 4f,
            radiusY = 4f,
        };
        SetBrushColor(0x22FFFFFFu, op);
        _dcTarget->FillRoundedRectangle(&rr, (ID2D1Brush*)_brush);

        bool hasBase = s.ArtBase != IntPtr.Zero && s.ArtBaseOpacity > 0.001f;
        bool hasTop = s.ArtTop != IntPtr.Zero && s.ArtTopOpacity > 0.001f;

        if (!hasBase && !hasTop)
        {
            if (_placeholderFormat != null)
            {
                SetBrushColor(s.LightTheme ? 0x66000000u : 0x88FFFFFFu, op);
                const string glyph = "\uE93C";
                fixed (char* pGlyph = glyph)
                    _dcTarget->DrawText(pGlyph, 1, _placeholderFormat, &rr.rect, (ID2D1Brush*)_brush,
                        D2D1_DRAW_TEXT_OPTIONS.D2D1_DRAW_TEXT_OPTIONS_NONE, DWRITE_MEASURING_MODE.DWRITE_MEASURING_MODE_NATURAL);
            }
            return;
        }

        ID2D1RoundedRectangleGeometry* clipGeo = null;
        try { _d2dFactory->CreateRoundedRectangleGeometry(&rr, &clipGeo); } catch { clipGeo = null; }

        if (clipGeo != null && _clipLayer != null)
        {
            var lp = new D2D1_LAYER_PARAMETERS
            {
                contentBounds = rr.rect,
                geometricMask = (ID2D1Geometry*)clipGeo,
                maskAntialiasMode = D2D1_ANTIALIAS_MODE.D2D1_ANTIALIAS_MODE_PER_PRIMITIVE,
                maskTransform = IdentityMatrix,
                opacity = 1f,
                opacityBrush = null,
                layerOptions = D2D1_LAYER_OPTIONS.D2D1_LAYER_OPTIONS_NONE,
            };
            _dcTarget->PushLayer(&lp, _clipLayer);

            if (hasBase) DrawBitmapUniformFill((ID2D1Bitmap*)s.ArtBase, rr.rect, s.ArtBaseOpacity * op);
            if (hasTop) DrawBitmapUniformFill((ID2D1Bitmap*)s.ArtTop, rr.rect, s.ArtTopOpacity * op);

            _dcTarget->PopLayer();
            clipGeo->Release();
        }
        else
        {
            if (clipGeo != null) clipGeo->Release();
            if (hasBase) DrawBitmapUniformFill((ID2D1Bitmap*)s.ArtBase, rr.rect, s.ArtBaseOpacity * op);
            if (hasTop) DrawBitmapUniformFill((ID2D1Bitmap*)s.ArtTop, rr.rect, s.ArtTopOpacity * op);
        }
    }

    private void DrawBitmapUniformFill(ID2D1Bitmap* bmp, D2D_RECT_F dst, float opacity)
    {
        var sz = bmp->GetSize();
        if (sz.width <= 0 || sz.height <= 0) return;
        float dstW = dst.right - dst.left;
        float dstH = dst.bottom - dst.top;
        float scale = Math.Max(dstW / sz.width, dstH / sz.height);
        float srcW = dstW / scale;
        float srcH = dstH / scale;
        float sx = (sz.width - srcW) * 0.5f;
        float sy = (sz.height - srcH) * 0.5f;
        var srcRect = new D2D_RECT_F { left = sx, top = sy, right = sx + srcW, bottom = sy + srcH };
        _dcTarget->DrawBitmap(bmp, &dst, opacity,
            D2D1_BITMAP_INTERPOLATION_MODE.D2D1_BITMAP_INTERPOLATION_MODE_LINEAR, &srcRect);
    }

    private void DrawTextStack(float x, float y, in WidgetRenderState s, float op)
    {
        float w = (float)s.TextWidthDip;
        if (w <= 1f) return;

        float topY = y + 1.5f;
        uint titleColor = s.LightTheme ? 0xFF000000u : 0xFFFFFFFFu;
        uint artistColor = s.LightTheme ? 0xFF484848u : 0xFFB3B3B3u;

        var titleClip = new D2D_RECT_F { left = x, top = topY, right = x + w, bottom = topY + 16f };
        _dcTarget->PushAxisAlignedClip(&titleClip, D2D1_ANTIALIAS_MODE.D2D1_ANTIALIAS_MODE_ALIASED);
        SetBrushColor(titleColor, op);
        if (s.MarqueeShowSecond)
        {
            float tx1 = x + s.MarqueeOffsetX;
            float tx2 = tx1 + s.MarqueeDistance;
            var r1 = new D2D_RECT_F { left = tx1, top = topY, right = tx1 + 10000f, bottom = topY + 18f };
            var r2 = new D2D_RECT_F { left = tx2, top = topY, right = tx2 + 10000f, bottom = topY + 18f };
            fixed (char* pTitle = s.Title)
            {
                _dcTarget->DrawText(pTitle, (uint)s.Title.Length, _titleNoTrimFormat, &r1, (ID2D1Brush*)_brush,
                    D2D1_DRAW_TEXT_OPTIONS.D2D1_DRAW_TEXT_OPTIONS_NONE, DWRITE_MEASURING_MODE.DWRITE_MEASURING_MODE_NATURAL);
                _dcTarget->DrawText(pTitle, (uint)s.Title.Length, _titleNoTrimFormat, &r2, (ID2D1Brush*)_brush,
                    D2D1_DRAW_TEXT_OPTIONS.D2D1_DRAW_TEXT_OPTIONS_NONE, DWRITE_MEASURING_MODE.DWRITE_MEASURING_MODE_NATURAL);
            }
        }
        else
        {
            var r = new D2D_RECT_F { left = x, top = topY, right = x + w, bottom = topY + 18f };
            fixed (char* pTitle = s.Title)
            {
                _dcTarget->DrawText(pTitle, (uint)s.Title.Length, _titleEllipsisFormat, &r, (ID2D1Brush*)_brush,
                    D2D1_DRAW_TEXT_OPTIONS.D2D1_DRAW_TEXT_OPTIONS_NONE, DWRITE_MEASURING_MODE.DWRITE_MEASURING_MODE_NATURAL);
            }
        }
        _dcTarget->PopAxisAlignedClip();

        if (!string.IsNullOrEmpty(s.Artist))
        {
            var artistRect = new D2D_RECT_F { left = x, top = topY + 16f, right = x + w, bottom = topY + 32f };
            SetBrushColor(artistColor, op);
            fixed (char* pArt = s.Artist)
            {
                _dcTarget->DrawText(pArt, (uint)s.Artist.Length, _artistFormat, &artistRect, (ID2D1Brush*)_brush,
                    D2D1_DRAW_TEXT_OPTIONS.D2D1_DRAW_TEXT_OPTIONS_CLIP, DWRITE_MEASURING_MODE.DWRITE_MEASURING_MODE_NATURAL);
            }
        }
    }

    private void DrawIconButton(float x, float y, float hover, bool pressed, bool light, float op)
    {
        float chromeOpacity = pressed ? 1f : hover;
        if (chromeOpacity <= 0.005f) return;

        var rr = new D2D1_ROUNDED_RECT
        {
            rect = new D2D_RECT_F { left = x + 0.5f, top = y + 0.5f, right = x + 24.5f, bottom = y + 33.5f },
            radiusX = 4f,
            radiusY = 4f,
        };

        uint fill = pressed
            ? (light ? 0x55FFFFFFu : 0x09FFFFFFu)
            : (light ? 0x99FFFFFFu : 0x10FFFFFFu);
        uint border = pressed
            ? (light ? 0x12000000u : 0x0EFFFFFFu)
            : (light ? 0x15000000u : 0x16FFFFFFu);

        SetBrushColor(fill, op * chromeOpacity);
        _dcTarget->FillRoundedRectangle(&rr, (ID2D1Brush*)_brush);
        SetBrushColor(border, op * chromeOpacity);
        _dcTarget->DrawRoundedRectangle(&rr, (ID2D1Brush*)_brush, 1f, null);
    }

    private void DrawVectorIcon(
        ID2D1PathGeometry* geo, float viewBoxSize, float targetSizeDip,
        float cx, float cy, float scaleMul,
        bool filled, bool stroked, uint colorArgb, float op)
    {
        if (geo == null) return;
        float scale = (targetSizeDip / viewBoxSize) * scaleMul;
        float half = viewBoxSize * 0.5f;
        var mat = ToD2D(Matrix3x2.CreateTranslation(-half, -half)
                      * Matrix3x2.CreateScale(scale)
                      * Matrix3x2.CreateTranslation(cx, cy));
        _dcTarget->SetTransform(&mat);
        SetBrushColor(colorArgb, op);
        if (filled)
            _dcTarget->FillGeometry((ID2D1Geometry*)geo, (ID2D1Brush*)_brush, null);
        if (stroked)
            _dcTarget->DrawGeometry((ID2D1Geometry*)geo, (ID2D1Brush*)_brush, 1.05f * (viewBoxSize / targetSizeDip), _roundStroke);
        var id = IdentityMatrix;
        _dcTarget->SetTransform(&id);
    }

    private void DrawProgressBar(float hitTopY, in WidgetRenderState s, float op)
    {
        float trackLeft = 1f;
        float trackRight = (float)s.WidthDip - 1f;
        float trackW = trackRight - trackLeft;
        if (trackW <= 2f) return;

        float h = (float)s.ProgressHeightDip;
        float r = (float)s.ProgressRadiusDip;
        float cy = hitTopY + 3.5f;
        float top = cy - h * 0.5f;
        float bottom = cy + h * 0.5f;

        var trackRect = new D2D1_ROUNDED_RECT
        {
            rect = new D2D_RECT_F { left = trackLeft, top = top, right = trackRight, bottom = bottom },
            radiusX = r,
            radiusY = r,
        };
        SetBrushColor(s.LightTheme ? 0x2E000000u : 0x33FFFFFFu, op);
        _dcTarget->FillRoundedRectangle(&trackRect, (ID2D1Brush*)_brush);

        if (s.ProgressHovered && !s.Scrubbing && s.ProgressHoverFraction > 0.001)
        {
            float hoverRight = trackLeft + trackW * (float)s.ProgressHoverFraction;
            var hoverRect = new D2D1_ROUNDED_RECT
            {
                rect = new D2D_RECT_F { left = trackLeft, top = top, right = hoverRight, bottom = bottom },
                radiusX = r,
                radiusY = r,
            };
            SetBrushColor(s.LightTheme ? 0x33000000u : 0x4DFFFFFFu, op);
            _dcTarget->FillRoundedRectangle(&hoverRect, (ID2D1Brush*)_brush);
        }

        if (s.ProgressFraction > 0.001)
        {
            float fillRight = trackLeft + trackW * (float)s.ProgressFraction;
            var fillRect = new D2D1_ROUNDED_RECT
            {
                rect = new D2D_RECT_F { left = trackLeft, top = top, right = fillRight, bottom = bottom },
                radiusX = r,
                radiusY = r,
            };
            uint fillColor = (s.ProgressHovered || s.Scrubbing)
                ? 0xFF1ED760u
                : (s.LightTheme ? 0xFF000000u : 0xFFFFFFFFu);
            SetBrushColor(fillColor, op);
            _dcTarget->FillRoundedRectangle(&fillRect, (ID2D1Brush*)_brush);
        }

        if (s.ProgressHovered && !s.Scrubbing)
        {
            float tickX = Math.Clamp(trackLeft + trackW * (float)s.ProgressHoverFraction - 1f, trackLeft, trackRight - 2f);
            var tickRect = new D2D1_ROUNDED_RECT
            {
                rect = new D2D_RECT_F { left = tickX, top = cy - 2.5f, right = tickX + 2f, bottom = cy + 2.5f },
                radiusX = 1f,
                radiusY = 1f,
            };
            SetBrushColor(s.LightTheme ? 0xFF1A1A1Au : 0xFFFFFFFFu, op * 0.9f);
            _dcTarget->FillRoundedRectangle(&tickRect, (ID2D1Brush*)_brush);
        }
        else if (s.Scrubbing)
        {
            float thumbCx = Math.Clamp(trackLeft + trackW * (float)s.ProgressFraction, trackLeft + 3.5f, trackRight - 3.5f);
            var ell = new D2D1_ELLIPSE
            {
                point = new D2D_POINT_2F { x = thumbCx, y = cy },
                radiusX = 3.5f,
                radiusY = 3.5f,
            };
            SetBrushColor(0xFFFFFFFFu, op);
            _dcTarget->FillEllipse(&ell, (ID2D1Brush*)_brush);
            SetBrushColor(s.LightTheme ? 0x55000000u : 0x33000000u, op);
            _dcTarget->DrawEllipse(&ell, (ID2D1Brush*)_brush, 0.5f, null);
        }
    }

    public (int WidthPx, int HeightPx) MeasureScrubPill(string posText, string durText, double dpiScale, out float posWidthDip)
    {
        posWidthDip = 0;
        if (!_initialized || _dwriteFactory == null || _pillPosFormat == null || _pillDurFormat == null)
            return (0, 0);

        float w1 = MeasureTabular(_pillPosFormat, posText);
        float w2 = MeasureTabular(_pillDurFormat, durText);
        posWidthDip = w1;
        float totalDipW = 8f + 8f + w1 + w2 + 8f + 8f;
        float totalDipH = 8f + 4f + 16f + 4f + 8f;
        return ((int)Math.Ceiling(totalDipW * dpiScale), (int)Math.Ceiling(totalDipH * dpiScale));
    }

    private float MeasureTabular(IDWriteTextFormat* fmt, string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        IDWriteTextLayout* layout = null;
        try
        {
            fixed (char* p = text)
                _dwriteFactory->CreateTextLayout(p, (uint)text.Length, fmt, 1000f, 100f, &layout);
            if (layout == null) return 0;
            if (_tabularTypography != null)
                layout->SetTypography(_tabularTypography, new DWRITE_TEXT_RANGE { startPosition = 0, length = (uint)text.Length });
            DWRITE_TEXT_METRICS m = default;
            layout->GetMetrics(&m);
            return m.widthIncludingTrailingWhitespace;
        }
        catch
        {
            return 0;
        }
        finally
        {
            if (layout != null) layout->Release();
        }
    }

    public void RenderScrubPill(
        LayeredSurface surface, HWND hwnd, Point* ptDst, int wPx, int hPx,
        string posText, string durText, float posWidthDip, bool lightTheme, double dpiScale)
    {
        if (!UiThread.IsOnUiThread)
        {
            Diag.Once("d2d-thread-pill", "RenderScrubPill called off the UI thread.");
            return;
        }
        if (!_initialized || _dcTarget == null || _brush == null || _dwriteFactory == null) return;
        if (!surface.EnsureSize(wPx, hPx)) return;

        bool drawing = false;
        try
        {
            var rc = new RECT { left = 0, top = 0, right = wPx, bottom = hPx };
            _dcTarget->BindDC(surface.MemDc, &rc);

            float dpi = (float)(96.0 * dpiScale);
            _dcTarget->SetDpi(dpi, dpi);
            _dcTarget->BeginDraw();
            drawing = true;
            var clear = new D2D1_COLOR_F { r = 0, g = 0, b = 0, a = 0 };
            _dcTarget->Clear(&clear);
            var id = IdentityMatrix;
            _dcTarget->SetTransform(&id);

            float wDip = (float)(wPx / dpiScale);
            float hDip = (float)(hPx / dpiScale);
            float pillLeft = 8f;
            float pillTop = 8f;
            float pillRight = wDip - 8f;
            float pillBottom = hDip - 8f;

            for (int i = 4; i >= 1; i--)
            {
                float pad = i * 1.4f;
                float alpha = 0.09f * (5 - i);
                var sr = new D2D1_ROUNDED_RECT
                {
                    rect = new D2D_RECT_F
                    {
                        left = pillLeft - pad * 0.5f,
                        top = pillTop + 1.5f - pad * 0.3f,
                        right = pillRight + pad * 0.5f,
                        bottom = pillBottom + 1.5f + pad * 0.7f,
                    },
                    radiusX = 6f + pad * 0.4f,
                    radiusY = 6f + pad * 0.4f,
                };
                SetBrushColor(0xFF000000u, alpha);
                _dcTarget->FillRoundedRectangle(&sr, (ID2D1Brush*)_brush);
            }

            var pill = new D2D1_ROUNDED_RECT
            {
                rect = new D2D_RECT_F { left = pillLeft, top = pillTop, right = pillRight, bottom = pillBottom },
                radiusX = 6f,
                radiusY = 6f,
            };
            SetBrushColor(lightTheme ? 0xFFF5F5F5u : 0xFF2C2C2Cu);
            _dcTarget->FillRoundedRectangle(&pill, (ID2D1Brush*)_brush);
            SetBrushColor(lightTheme ? 0x22000000u : 0x2EFFFFFFu);
            _dcTarget->DrawRoundedRectangle(&pill, (ID2D1Brush*)_brush, 1f, null);

            DrawTabularText(posText, _pillPosFormat, pillLeft + 8f, pillTop + 4f,
                lightTheme ? 0xFF1A1A1Au : 0xFFFFFFFFu);
            DrawTabularText(durText, _pillDurFormat, pillLeft + 8f + posWidthDip, pillTop + 4f,
                lightTheme ? 0xFF606060u : 0xFFB3B3B3u);

            _dcTarget->EndDraw();
            drawing = false;
            surface.Present(hwnd, ptDst);
        }
        catch (Exception ex)
        {
            Diag.Log("RenderScrubPill error: " + ex);
        }
        finally
        {
            if (drawing)
            {
                try { _dcTarget->EndDraw(); } catch { }
            }
        }
    }

    private void DrawTabularText(string text, IDWriteTextFormat* fmt, float x, float y, uint colorArgb)
    {
        if (string.IsNullOrEmpty(text) || fmt == null) return;
        IDWriteTextLayout* layout = null;
        try
        {
            fixed (char* p = text)
                _dwriteFactory->CreateTextLayout(p, (uint)text.Length, fmt, 1000f, 100f, &layout);
            if (layout == null) return;
            if (_tabularTypography != null)
                layout->SetTypography(_tabularTypography, new DWRITE_TEXT_RANGE { startPosition = 0, length = (uint)text.Length });
            SetBrushColor(colorArgb);
            _dcTarget->DrawTextLayout(new D2D_POINT_2F { x = x, y = y }, layout, (ID2D1Brush*)_brush,
                D2D1_DRAW_TEXT_OPTIONS.D2D1_DRAW_TEXT_OPTIONS_NONE);
        }
        catch { }
        finally
        {
            if (layout != null) layout->Release();
        }
    }

    private void SetBrushColor(uint argb, float alphaMul = 1f)
    {
        float a = ((argb >> 24) & 0xFF) / 255f * alphaMul;
        float r = ((argb >> 16) & 0xFF) / 255f;
        float g = ((argb >> 8) & 0xFF) / 255f;
        float b = (argb & 0xFF) / 255f;
        var c = new D2D1_COLOR_F { r = r, g = g, b = b, a = a };
        _brush->SetColor(&c);
    }

    public void Dispose()
    {
        if (!_initialized) return;
        _initialized = false;

        if (_playGeo != null) { _playGeo->Release(); _playGeo = null; }
        if (_pauseGeo != null) { _pauseGeo->Release(); _pauseGeo = null; }
        if (_prevGeo != null) { _prevGeo->Release(); _prevGeo = null; }
        if (_nextGeo != null) { _nextGeo->Release(); _nextGeo = null; }
        if (_addCircleGeo != null) { _addCircleGeo->Release(); _addCircleGeo = null; }
        if (_checkCircleGeo != null) { _checkCircleGeo->Release(); _checkCircleGeo = null; }

        if (_tabularTypography != null) { _tabularTypography->Release(); _tabularTypography = null; }
        if (_titleEllipsisFormat != null) { _titleEllipsisFormat->Release(); _titleEllipsisFormat = null; }
        if (_titleNoTrimFormat != null) { _titleNoTrimFormat->Release(); _titleNoTrimFormat = null; }
        if (_artistFormat != null) { _artistFormat->Release(); _artistFormat = null; }
        if (_placeholderFormat != null) { _placeholderFormat->Release(); _placeholderFormat = null; }
        if (_pillPosFormat != null) { _pillPosFormat->Release(); _pillPosFormat = null; }
        if (_pillDurFormat != null) { _pillDurFormat->Release(); _pillDurFormat = null; }
        if (_dwriteFactory != null) { _dwriteFactory->Release(); _dwriteFactory = null; }

        if (_clipLayer != null) { _clipLayer->Release(); _clipLayer = null; }
        if (_roundStroke != null) { _roundStroke->Release(); _roundStroke = null; }
        if (_brush != null) { _brush->Release(); _brush = null; }
        if (_dcTarget != null) { _dcTarget->Release(); _dcTarget = null; }
        if (_d2dFactory != null) { _d2dFactory->Release(); _d2dFactory = null; }
    }
}
