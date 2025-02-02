﻿using System;
using System.Linq;
using System.Collections.Generic;

namespace HyperVLauncher.Contracts.Models
{
    public class AppSettings
    {
        public bool StartOnLogin { get; set; } = true;
        public bool NotifyOnNewVm { get; set; } = true;
        public bool AutoCreateShortcuts { get; set; }
        public bool AutoDeleteShortcuts { get; set; } = true;
        public bool AutoCreateDesktopShortcut { get; set; } = true;
        public bool AutoCreateStartMenuShortcut { get; set; } = true;

        public List<Shortcut> Shortcuts { get; } = new();

        public static Shortcut CreateShortcut(string name, string vmId)
        {
            var id = Guid.NewGuid().ToString();
            return new Shortcut(id, vmId, name);
        }

        public Shortcut? GetShortcut(string id)
        {
            return Shortcuts.FirstOrDefault(x => x.Id == id);
        }

        public void DeleteShortcut(string id)
        {
            var itemToRemove = GetShortcut(id);

            if (itemToRemove != null)
            {
                Shortcuts.Remove(itemToRemove);
            }
        }
    }
}
