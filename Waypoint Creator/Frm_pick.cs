using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Frm_waypoint
{
    /// <summary>
    /// Which entry did you mean. A name search hits several - "Spider" alone is dozens - and
    /// picking the wrong one wastes a whole load, so the counts are shown and the busiest is
    /// preselected.
    /// </summary>
    internal class Frm_pick : Form
    {
        private readonly ListBox _list = new ListBox();

        private object _chosenItem;

        private Frm_pick(System.Collections.IEnumerable items, string title)
        {
            Text = title;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.SizableToolWindow;
            ClientSize = new Size(560, 380);
            MinimizeBox = false;
            MaximizeBox = false;

            _list.Dock = DockStyle.Fill;
            _list.Font = new Font("Consolas", 9F);
            _list.IntegralHeight = false;
            foreach (var h in items) _list.Items.Add(h);
            if (_list.Items.Count > 0) _list.SelectedIndex = 0;
            _list.DoubleClick += delegate { Accept(); };
            _list.KeyDown += delegate (object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Accept(); }
            };

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                Height = 44,
                Padding = new Padding(6)
            };
            var ok = new Button { Text = "Load", DialogResult = DialogResult.OK, Width = 90 };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90 };
            ok.Click += delegate { Accept(); };
            buttons.Controls.Add(ok);
            buttons.Controls.Add(cancel);

            Controls.Add(_list);
            Controls.Add(buttons);
            AcceptButton = ok;
            CancelButton = cancel;
        }

        private void Accept()
        {
            _chosenItem = _list.SelectedItem;
            DialogResult = DialogResult.OK;
            Close();
        }

        public static T Choose<T>(IWin32Window owner, IList<T> items, string title) where T : class
        {
            if (items == null || items.Count == 0) return null;
            if (items.Count == 1) return items[0];
            using (var dlg = new Frm_pick(items, title))
                return dlg.ShowDialog(owner) == DialogResult.OK ? dlg._chosenItem as T : null;
        }
    }
}
