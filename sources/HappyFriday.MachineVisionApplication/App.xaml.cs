using HappyFriday.MachineVisionApplication.Views;
using Prism.Ioc;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace HappyFriday.MachineVisionApplication
{
    public partial class App
    {
        protected override void RegisterTypes(IContainerRegistry containerRegistry)
        {
        }

        protected override System.Windows.Window CreateShell()
        {
            return this.Container.Resolve<MainWindowView>();
        }
    }
}
