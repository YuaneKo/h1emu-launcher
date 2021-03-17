namespace JSLaunchPad
{
    partial class MainForm
    {
        /// <summary>
        /// Variable nécessaire au concepteur.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Nettoyage des ressources utilisées.
        /// </summary>
        /// <param name="disposing">true si les ressources managées doivent être supprimées ; sinon, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Code généré par le Concepteur Windows Form

        /// <summary>
        /// Méthode requise pour la prise en charge du concepteur - ne modifiez pas
        /// le contenu de cette méthode avec l'éditeur de code.
        /// </summary>
        private void InitializeComponent()
        {
            this.MainBrowser = new System.Windows.Forms.WebBrowser();
            this.SuspendLayout();
            // 
            // MainBrowser
            // 
            this.MainBrowser.AllowWebBrowserDrop = false;
            this.MainBrowser.Dock = System.Windows.Forms.DockStyle.Fill;
            this.MainBrowser.IsWebBrowserContextMenuEnabled = false;
            this.MainBrowser.Location = new System.Drawing.Point(0, 0);
            this.MainBrowser.Margin = new System.Windows.Forms.Padding(0);
            this.MainBrowser.MinimumSize = new System.Drawing.Size(20, 20);
            this.MainBrowser.Name = "MainBrowser";
            this.MainBrowser.ScriptErrorsSuppressed = true;
            this.MainBrowser.ScrollBarsEnabled = false;
            this.MainBrowser.Size = new System.Drawing.Size(860, 524);
            this.MainBrowser.TabIndex = 0;
            this.MainBrowser.Url = new System.Uri("https://dev.h1emu.com/lp.php?gameid=js-live", System.UriKind.Absolute);
            this.MainBrowser.WebBrowserShortcutsEnabled = false;
            this.MainBrowser.DocumentCompleted += new System.Windows.Forms.WebBrowserDocumentCompletedEventHandler(this.MainBrowser_DocumentCompleted);
            // 
            // MainForm
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.AutoSize = true;
            this.ClientSize = new System.Drawing.Size(860, 524);
            this.Controls.Add(this.MainBrowser);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.Name = "MainForm";
            this.Text = "H1EMU:JS LaunchPad";
            this.Load += new System.EventHandler(this.MainForm_Load);
            this.ResumeLayout(false);

        }

        #endregion

        private System.Windows.Forms.WebBrowser MainBrowser;
    }
}

