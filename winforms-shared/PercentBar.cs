using System;
using System.Drawing;
using System.Windows.Forms;

namespace Winzilla
{
    /// <summary>Progress bar with the percentage drawn on top (the stock ProgressBar can't show text).</summary>
    internal sealed class PercentBar : Control
    {
        private static readonly Color FillColor = Color.FromArgb(6, 176, 37);
        private double _value;

        public PercentBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
        }

        /// <summary>0–100.</summary>
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public double Value
        {
            get => _value;
            set
            {
                value = Math.Max(0, Math.Min(100, value));
                if (value == _value) return;
                _value = value;
                Invalidate();
            }
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var bounds = ClientRectangle;
            e.Graphics.Clear(SystemColors.Window);

            int fill = (int)Math.Round((bounds.Width - 2) * _value / 100);
            if (fill > 0)
                using (var brush = new SolidBrush(FillColor))
                    e.Graphics.FillRectangle(brush, 1, 1, fill, bounds.Height - 2);

            ControlPaint.DrawBorder(e.Graphics, bounds, SystemColors.ControlDark, ButtonBorderStyle.Solid);
            TextRenderer.DrawText(e.Graphics, Text, Font, bounds, SystemColors.ControlText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }
}
