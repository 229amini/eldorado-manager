using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace SellerDesk
{
    /// <summary>
    /// A countdown with fixed digit cells. Big Shoulders has no tabular numerals and its "1" is about
    /// half as wide as the other digits, so plain text would shift on every tick.
    /// </summary>
    public sealed class Digits : FrameworkElement
    {
        private static readonly Typeface Face = new Typeface(
            new FontFamily(new Uri("pack://application:,,,/"), "./Fonts/#Big Shoulders"),
            FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
            nameof(Text), typeof(string), typeof(Digits),
            new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
            nameof(Fill), typeof(Brush), typeof(Digits),
            new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
            nameof(Size), typeof(double), typeof(Digits),
            new FrameworkPropertyMetadata(48.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

        public string Text
        {
            get => (string)GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }

        public Brush Fill
        {
            get => (Brush)GetValue(FillProperty);
            set => SetValue(FillProperty, value);
        }

        public double Size
        {
            get => (double)GetValue(SizeProperty);
            set => SetValue(SizeProperty, value);
        }

        private FormattedText Format(char c) => new FormattedText(
            c.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, Size, Fill,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        private double Cell(char c, double digit) => char.IsDigit(c) ? digit : Format(c).WidthIncludingTrailingWhitespace;

        private double DigitWidth => "0123456789".Max(c => Format(c).WidthIncludingTrailingWhitespace);

        protected override Size MeasureOverride(Size available)
        {
            var digit = DigitWidth;
            return new Size((Text ?? "").Sum(c => Cell(c, digit)), Format('0').Height);
        }

        protected override void OnRender(DrawingContext context)
        {
            var digit = DigitWidth;
            var x = 0.0;
            foreach (var c in Text ?? "")
            {
                var glyph = Format(c);
                var cell = Cell(c, digit);
                context.DrawText(glyph, new Point(x + (cell - glyph.WidthIncludingTrailingWhitespace) / 2, 0));
                x += cell;
            }
        }
    }
}
