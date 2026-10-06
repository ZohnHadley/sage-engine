#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Sage.UI;

// A line (or lines, split at '\n') of text, as big as ITextMeasure says it is. TextAlign is for the
// renderer: where the text sits when the label is given more room than it measured.
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public class Label : Widget
{
    private string _text = "";
    private float _textScale = 1f;
    private Align _textAlign = Align.Start;

    public Label() { }
    public Label(string text) { _text = text ?? ""; }

    public override string TypeName => "label";

    public string Text
    {
        get => _text;
        set { value ??= ""; if (string.Equals(_text, value, StringComparison.Ordinal)) return; _text = value; InvalidateMeasure(); }
    }

    public float TextScale { get => _textScale; set { if (_textScale == value) return; _textScale = value; InvalidateMeasure(); } }

    public Align TextAlign { get => _textAlign; set { if (_textAlign == value) return; _textAlign = value; InvalidateVisual(); } }

    protected override Vector2 MeasureContent(Vector2 available, ITextMeasure text) =>
        _text.Length == 0 ? new Vector2(0f, text.LineHeight * _textScale) : text.Measure(_text, _textScale);

    // Where the renderer places the text: the content rect, less a checkbox's box or a dropdown's arrow.
    internal virtual Rect TextArea => ContentRect;
}

// A label that takes focus and does something when confirmed or clicked (Pressed). Disabled, it keeps
// its place in the layout and is skipped by navigation.
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public class Button : Label
{
    public Button() { Focusable = true; TextAlign = Align.Center; }
    public Button(string text) : base(text) { Focusable = true; TextAlign = Align.Center; }

    public override string TypeName => "button";

    public event Action<Button>? Pressed;

    protected internal override void OnActivate()
    {
        if (IsEnabled) Pressed?.Invoke(this);
    }
}

// The root's tooltip (UiRoot.Tooltip): text shown near the widget the pointer rests on, or the focused
// one when the gamepad or keyboard moved focus last, after Delay seconds. Positioned by the root, below
// its target and kept inside the screen; never hit by the pointer.
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public sealed class Tooltip : Label
{
    internal Tooltip()
    {
        Visible = false;
        HitTestable = false;
    }

    public override string TypeName => "tooltip";

    // Seconds the pointer (or focus) has to stay on a widget before its tooltip shows.
    public float Delay { get; set; } = 0.5f;

    // Virtual units between the target's bottom edge and the tooltip.
    public float Offset { get; set; } = 4f;

    // The widget whose TooltipText is shown, while Visible.
    public Widget? Target { get; internal set; }
}

// A picture, by asset path (the renderer resolves it, #97), at NaturalSize unless its container
// stretches it. With KeepAspect the drawn part (ImageRect) keeps the natural proportions, centred.
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public class Image : Widget
{
    private string? _source;
    private Vector2 _naturalSize;
    private bool _keepAspect = true;

    public Image() { }
    public Image(string? source, Vector2 naturalSize) { _source = source; _naturalSize = naturalSize; }

    public override string TypeName => "image";

    public string? Source { get => _source; set { if (_source == value) return; _source = value; InvalidateVisual(); } }

    // Its size in virtual units when nothing stretches it: the layout cannot open the texture to ask.
    public Vector2 NaturalSize { get => _naturalSize; set { if (_naturalSize == value) return; _naturalSize = value; InvalidateMeasure(); } }

    public bool KeepAspect { get => _keepAspect; set { if (_keepAspect == value) return; _keepAspect = value; InvalidateArrange(); } }

    // Where the picture itself is drawn: the content rect, or the largest part of it with the natural aspect.
    public Rect ImageRect { get; private set; }

    protected override Vector2 MeasureContent(Vector2 available, ITextMeasure text) => _naturalSize;

    protected override void ArrangeContent(Rect content)
    {
        if (!_keepAspect || _naturalSize.X <= 0f || _naturalSize.Y <= 0f) { ImageRect = content; return; }
        float scale = MathF.Min(content.Width / _naturalSize.X, content.Height / _naturalSize.Y);
        float w = _naturalSize.X * scale, h = _naturalSize.Y * scale;
        ImageRect = new Rect(content.X + (content.Width - w) * 0.5f, content.Y + (content.Height - h) * 0.5f, w, h);
    }
}

// A value between Min and Max shown as a filled part (health, stamina, a cast bar, a loading bar). Its
// size comes from MinSize or its container; a Row fills left to right and a Column bottom to top.
// Changing Value re-lays nothing out: FillRect is worked out from the arranged rect when asked.
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public class Bar : Widget
{
    private float _min, _max = 1f, _value;
    private Orientation _direction = Orientation.Row;

    public override string TypeName => "bar";

    public float Min { get => _min; set { if (_min == value) return; _min = value; InvalidateVisual(); } }
    public float Max { get => _max; set { if (_max == value) return; _max = value; InvalidateVisual(); } }
    public float Value { get => _value; set { if (_value == value) return; _value = value; InvalidateVisual(); } }
    public Orientation Direction { get => _direction; set { if (_direction == value) return; _direction = value; InvalidateVisual(); } }

    // Value's place between Min and Max, 0..1.
    public float Fraction => _max > _min ? Math.Clamp((_value - _min) / (_max - _min), 0f, 1f) : 0f;

    // The filled part of the content rect.
    public Rect FillRect
    {
        get
        {
            var c = ContentRect;
            float f = Fraction;
            return _direction == Orientation.Row
                ? new Rect(c.X, c.Y, c.Width * f, c.Height)
                : new Rect(c.X, c.Bottom - c.Height * f, c.Width, c.Height * f);
        }
    }
}

// A viewport onto one child (Content) that may be bigger than it, scrolled by Offset, clipping what is
// outside (Godot's ScrollContainer). Vertical by default. It measures only as big as its content across
// the scrolled axis — along it, as MinSize or its container says — and focus moving onto something
// inside scrolls it into view.
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public class Scroll : Widget
{
    private Widget? _content;
    private Vector2 _offset;
    private bool _horizontal, _vertical = true;
    private Vector2 _contentSize;

    public override string TypeName => "scroll";

    public Widget? Content
    {
        get => _content;
        set
        {
            if (_content == value) return;
            if (_content != null) RemoveChild(_content);
            _content = null;
            if (value == null) return;
            AddChild(value);   // throws, leaving no content, if it already has a parent
            _content = value;
        }
    }

    public bool Horizontal { get => _horizontal; set { if (_horizontal == value) return; _horizontal = value; InvalidateMeasure(); } }
    public bool Vertical { get => _vertical; set { if (_vertical == value) return; _vertical = value; InvalidateMeasure(); } }

    // Virtual units per wheel notch (UiInput.Wheel).
    public float WheelStep { get; set; } = 40f;

    // How far the content is scrolled, from its top-left; kept within 0..MaxOffset by layout.
    public Vector2 Offset
    {
        get => _offset;
        set { if (_offset == value) return; _offset = value; InvalidateArrange(); }
    }

    // How far it can scroll, as of the last layout.
    public Vector2 MaxOffset { get; private set; }

    protected override Rect ChildClip => RectMath.Intersect(Clip, ContentRect);

    protected override Vector2 MeasureContent(Vector2 available, ITextMeasure text)
    {
        if (_content == null) return Vector2.Zero;
        var room = new Vector2(_horizontal ? float.PositiveInfinity : available.X, _vertical ? float.PositiveInfinity : available.Y);
        _contentSize = MeasureChild(_content, room, text);
        return new Vector2(_horizontal ? 0f : _contentSize.X, _vertical ? 0f : _contentSize.Y);
    }

    protected override void ArrangeContent(Rect content)
    {
        if (_content == null) { MaxOffset = Vector2.Zero; return; }
        var size = new Vector2(_horizontal ? MathF.Max(content.Width, _contentSize.X) : content.Width,
                               _vertical ? MathF.Max(content.Height, _contentSize.Y) : content.Height);
        MaxOffset = new Vector2(size.X - content.Width, size.Y - content.Height);
        _offset = Vector2.Clamp(_offset, Vector2.Zero, MaxOffset);
        ArrangeChild(_content, new Rect(content.X - _offset.X, content.Y - _offset.Y, size.X, size.Y));
    }

    // The offset that shows [start, start + length) in a viewport `view` long, moving `offset` least.
    private static float Reveal(float start, float length, float view, float offset)
    {
        if (start + length > offset + view) offset = start + length - view;
        if (start < offset) offset = start;
        return offset;
    }

    public void ScrollBy(Vector2 delta) => Offset = Vector2.Clamp(_offset + delta, Vector2.Zero, MaxOffset);

    // Scrolls the least that shows `widget` (one inside this) whole, or its top-left when it is bigger
    // than the viewport. Uses the last layout.
    public void ScrollIntoView(Widget widget)
    {
        if (widget == this || !Contains(widget)) return;
        var view = ContentRect;
        var r = widget.Rect;
        var offset = _offset;
        // Where the widget is in the content, measured from the viewport's top-left at offset zero.
        if (_vertical) offset.Y = Reveal(r.Y - view.Y + _offset.Y, r.Height, view.Height, offset.Y);
        if (_horizontal) offset.X = Reveal(r.X - view.X + _offset.X, r.Width, view.Width, offset.X);
        Offset = Vector2.Clamp(offset, Vector2.Zero, MaxOffset);
    }
}
