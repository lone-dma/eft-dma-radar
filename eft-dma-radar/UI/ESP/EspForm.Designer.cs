namespace eft_dma_radar.UI.ESP
{
    partial class EspForm
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            ComponentResourceManager resources = new ComponentResourceManager(typeof(EspForm));
            skglControl_ESP = new SKGLControl();
            SuspendLayout();
            // 
            // skglControl_ESP
            // 
            skglControl_ESP.BackColor = Color.Black;
            skglControl_ESP.Dock = DockStyle.Fill;
            skglControl_ESP.Location = new Point(0, 0);
            skglControl_ESP.Margin = new Padding(4, 3, 4, 3);
            skglControl_ESP.Name = "skglControl_ESP";
            skglControl_ESP.Size = new Size(624, 441);
            skglControl_ESP.TabIndex = 0;
            skglControl_ESP.VSync = false;
            // 
            // EspForm
            // 
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(624, 441);
            Controls.Add(skglControl_ESP);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            Icon = (Icon)resources.GetObject("$this.Icon");
            MinimizeBox = false;
            Name = "EspForm";
            Text = "ESP";
            TopMost = false;
            ResumeLayout(false);
        }

        #endregion

        private SKGLControl skglControl_ESP;
    }
}