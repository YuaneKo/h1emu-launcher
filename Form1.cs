using System;
using System.Windows.Forms;

namespace JSLaunchPad
{
    public partial class MainForm : Form
    {
       
        public MainForm()
        {
            InitializeComponent();
        }

        private void MainForm_Load(object sender, EventArgs e)
        {
            DialogResult launchmsg = MessageBox.Show("This project is not fully working. Please be patient.", "Confirmation", MessageBoxButtons.OK, MessageBoxIcon.Information);
            if (launchmsg == DialogResult.OK) { }





            /*if (launchmsg == DialogResult.Cancel)*/
        }

 
        private void MainBrowser_DocumentCompleted(object sender, WebBrowserDocumentCompletedEventArgs e)
        {

        }

    }
}
