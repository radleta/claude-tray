using System.Globalization;
using Imrdy.Core.Icons;
using Imrdy.Core.Overlay;

namespace Imrdy.Core.Menus;

internal static class ControllerMenuModel
{
    public static IReadOnlyList<MenuItemModel> Build(ControllerMenuState state)
    {
        var items = new List<MenuItemModel>
        {
            new() { Label = "imrdy", Enabled = false },
            new() { Type = MenuItemType.Separator },
        };

        items.Add(BuildSoundSubmenu(state));
        items.Add(BuildTraySubmenu(state));
        items.Add(BuildOverlaySubmenu(state));

        items.Add(new MenuItemModel { Type = MenuItemType.Separator });

        items.Add(BuildSessionItems(state));
        items.Add(BuildWorkspaceItems(state));

        items.Add(new MenuItemModel { Type = MenuItemType.Separator });

        items.Add(new MenuItemModel { Label = "Connections…", Tag = "open-connections" });
        items.Add(BuildManageSubmenu(state));

        items.Add(new MenuItemModel { Type = MenuItemType.Separator });

        items.Add(new MenuItemModel { Label = "Exit", Tag = "exit" });

        return items;
    }

    private static MenuItemModel BuildSoundSubmenu(ControllerMenuState state)
    {
        var children = new List<MenuItemModel>
        {
            new MenuItemModel
            {
                Label = "Sounds",
                Tag = "toggle-sound",
                Checked = state.Config.Sound.Enabled,
            },
            BuildSoundPackSubmenu(state),
            BuildEnabledPacksSubmenu(state),
        };

        return new MenuItemModel
        {
            Label = "Sound",
            Type = MenuItemType.Submenu,
            Children = children,
        };
    }

    private static MenuItemModel BuildTraySubmenu(ControllerMenuState state)
    {
        var children = new List<MenuItemModel>
        {
            new MenuItemModel
            {
                Label = "Enabled",
                Tag = "toggle-tray",
                Checked = state.Config.Tray.Enabled,
            },
            new MenuItemModel { Type = MenuItemType.Separator },
            BuildIconStyleSubmenu(state),
        };

        return new MenuItemModel
        {
            Label = "Tray",
            Type = MenuItemType.Submenu,
            Children = children,
        };
    }

    public static MenuItemModel BuildOverlaySubmenu(ControllerMenuState state)
    {
        var overlay = state.Config.Overlay;
        var currentAnchor = OverlayAnchor.Parse(overlay.Position);

        // D7 Checked-state contract (free-float offset is the source of truth once set):
        //   offset present → Checked when it equals THIS anchor's resolved offset for the
        //     target monitor (same OverlayPlacement.AnchorToOffset geometry the position-
        //     preset write uses — see ControllerMenuBuilder.TryHandleOverlayTag);
        //   offset null     → Checked when the legacy Position string names this anchor.
        // Core stays WinForms-free: workingArea/panelSize come from state (populated by the
        // Windows layer via Screen.AllScreens), never a direct Screen reference.
        bool IsPositionChecked(string anchorString)
        {
            if (overlay.OffsetX.HasValue && overlay.OffsetY.HasValue)
            {
                var (anchorOffsetX, anchorOffsetY) = OverlayPlacement.AnchorToOffset(
                    anchorString, state.OverlayWorkingArea, state.OverlayPanelSize);
                return overlay.OffsetX.Value == anchorOffsetX && overlay.OffsetY.Value == anchorOffsetY;
            }
            return currentAnchor == OverlayAnchor.Parse(anchorString);
        }

        var children = new List<MenuItemModel>
        {
            // (1) toggle
            new MenuItemModel { Label = "Enabled", Tag = "toggle-overlay", Checked = overlay.Enabled },
            // (2) sep
            new MenuItemModel { Type = MenuItemType.Separator },
            // (3) 6 position anchors
            new MenuItemModel { Label = "Top Left",      Tag = "set-overlay-position:top-left",      Checked = IsPositionChecked("top-left") },
            new MenuItemModel { Label = "Top Center",    Tag = "set-overlay-position:top-center",    Checked = IsPositionChecked("top-center") },
            new MenuItemModel { Label = "Top Right",     Tag = "set-overlay-position:top-right",     Checked = IsPositionChecked("top-right") },
            new MenuItemModel { Label = "Bottom Left",   Tag = "set-overlay-position:bottom-left",   Checked = IsPositionChecked("bottom-left") },
            new MenuItemModel { Label = "Bottom Center", Tag = "set-overlay-position:bottom-center", Checked = IsPositionChecked("bottom-center") },
            new MenuItemModel { Label = "Bottom Right",  Tag = "set-overlay-position:bottom-right",  Checked = IsPositionChecked("bottom-right") },
            // (4) sep
            new MenuItemModel { Type = MenuItemType.Separator },
            // (5) size presets
            new MenuItemModel { Label = "Small (48px)",        Tag = "set-overlay-size:48",  Checked = overlay.Size == 48 },
            new MenuItemModel { Label = "Medium (64px)",       Tag = "set-overlay-size:64",  Checked = overlay.Size == 64 },
            new MenuItemModel { Label = "Large (96px)",        Tag = "set-overlay-size:96",  Checked = overlay.Size == 96 },
            new MenuItemModel { Label = "Extra Large (128px)", Tag = "set-overlay-size:128", Checked = overlay.Size == 128 },
            // (6) sep
            new MenuItemModel { Type = MenuItemType.Separator },
            // (7) spacing presets
            new MenuItemModel { Label = "Spacing: 4px",  Tag = "set-overlay-spacing:4",  Checked = overlay.Spacing == 4 },
            new MenuItemModel { Label = "Spacing: 8px",  Tag = "set-overlay-spacing:8",  Checked = overlay.Spacing == 8 },
            new MenuItemModel { Label = "Spacing: 12px", Tag = "set-overlay-spacing:12", Checked = overlay.Spacing == 12 },
            new MenuItemModel { Label = "Spacing: 16px", Tag = "set-overlay-spacing:16", Checked = overlay.Spacing == 16 },
            // (8) sep
            new MenuItemModel { Type = MenuItemType.Separator },
        };

        // (9) monitor selector — one item per monitor label
        for (var i = 0; i < state.Monitors.Count; i++)
        {
            children.Add(new MenuItemModel
            {
                Label = state.Monitors[i],
                Tag = $"set-overlay-monitor:{i}",
                Checked = overlay.Monitor == i,
            });
        }

        // (10) sep
        children.Add(new MenuItemModel { Type = MenuItemType.Separator });
        // (11) lock toggle
        children.Add(new MenuItemModel { Label = "Lock Position", Tag = "toggle-overlay-lock", Checked = overlay.Locked });

        return new MenuItemModel
        {
            Label = "Overlay",
            Type = MenuItemType.Submenu,
            Children = children,
        };
    }

    private static MenuItemModel BuildSessionItems(ControllerMenuState state)
    {
        var children = new List<MenuItemModel>();

        var sorted = state.Sessions
            .OrderBy(s => s.DesktopIndex.HasValue ? 0 : 1)
            .ThenBy(s => s.DesktopIndex ?? 0);

        foreach (var s in sorted)
        {
            children.Add(new MenuItemModel
            {
                Label = $"{s.Project ?? s.SessionId} [{s.Status}]",
                Tag = $"switch-session:{s.SessionId}",
                Enabled = true,
            });
        }

        if (children.Count == 0)
        {
            children.Add(new MenuItemModel { Label = "(no active sessions)", Enabled = false });
        }

        return new MenuItemModel
        {
            Label = $"Sessions ({state.Sessions.Count})",
            Type = MenuItemType.Submenu,
            Children = children,
        };
    }

    private static MenuItemModel BuildWorkspaceItems(ControllerMenuState state)
    {
        var children = new List<MenuItemModel>();

        var sorted = state.Workspaces
            .OrderBy(ws => ws.DesktopIndex);

        foreach (var ws in sorted)
        {
            children.Add(new MenuItemModel
            {
                Label = ws.WorkspaceName,
                Tag = $"switch-workspace:{ws.WorkspacePath}",
                Enabled = true,
            });
        }

        if (children.Count == 0)
        {
            children.Add(new MenuItemModel { Label = "(no workspaces)", Enabled = false });
        }

        return new MenuItemModel
        {
            Label = "Workspaces",
            Type = MenuItemType.Submenu,
            Children = children,
        };
    }

    private static MenuItemModel BuildManageSubmenu(ControllerMenuState state)
    {
        var children = new List<MenuItemModel>
        {
            new MenuItemModel { Label = "Open Config Folder", Tag = "open-config" },
            new MenuItemModel { Label = "View Log", Tag = "open-log" },
        };

        if (state.DevBuild is { } dev)
        {
            children.Add(new MenuItemModel { Type = MenuItemType.Separator });
            children.Add(BuildDevSubmenu(dev));
        }

        return new MenuItemModel
        {
            Label = "Manage",
            Type = MenuItemType.Submenu,
            Children = children,
        };
    }

    private static MenuItemModel BuildDevSubmenu(DevBuildState dev)
    {
        var children = new List<MenuItemModel>();

        if (dev.Fixtures.Count == 0)
        {
            children.Add(new MenuItemModel { Label = "(no fixtures found)", Enabled = false });
        }
        else
        {
            foreach (var fixture in dev.Fixtures)
            {
                children.Add(new MenuItemModel
                {
                    Label = fixture.DisplayName,
                    Tag = $"dev-preview:{fixture.FullPath}",
                });
            }
        }

        children.Add(new MenuItemModel { Type = MenuItemType.Separator });
        children.Add(new MenuItemModel
        {
            Label = dev.RunningPreviewCount > 0
                ? $"Close All ({dev.RunningPreviewCount})"
                : "Close All",
            Tag = "dev-preview-close-all",
            Enabled = dev.RunningPreviewCount > 0,
        });

        return new MenuItemModel
        {
            Label = "Dev",
            Type = MenuItemType.Submenu,
            Children = children,
        };
    }

    private static MenuItemModel BuildSoundPackSubmenu(ControllerMenuState state)
    {
        var children = new List<MenuItemModel>();
        var defaultPack = state.Config.Sound.DefaultPack;
        var isRandom = string.Equals(defaultPack, "random", StringComparison.OrdinalIgnoreCase);
        var isNone = string.IsNullOrEmpty(defaultPack);

        // Random option
        children.Add(new MenuItemModel
        {
            Label = "Random",
            Tag = "switch-pack:random",
            Checked = isRandom,
        });

        // Individual packs
        foreach (var pack in state.InstalledPacks)
        {
            children.Add(new MenuItemModel
            {
                Label = pack,
                Tag = $"switch-pack:{pack}",
                Checked = !isRandom && !isNone
                    && string.Equals(pack, defaultPack, StringComparison.OrdinalIgnoreCase),
            });
        }

        if (state.InstalledPacks.Count > 0)
        {
            children.Add(new MenuItemModel { Type = MenuItemType.Separator });
        }

        // None option
        children.Add(new MenuItemModel
        {
            Label = "(None)",
            Tag = "switch-pack:",
            Checked = isNone,
        });

        return new MenuItemModel
        {
            Label = "Sound Pack",
            Type = MenuItemType.Submenu,
            Children = children,
        };
    }

    private static MenuItemModel BuildIconStyleSubmenu(ControllerMenuState state)
    {
        var children = new List<MenuItemModel>();
        var currentStyle = StyleNames.NormalizeStyleName(state.Config.Tray.IconStyle) ?? "circles";
        var textInfo = CultureInfo.InvariantCulture.TextInfo;

        // Built-in shape styles
        foreach (var styleName in StyleNames.BuiltInStyles)
        {
            children.Add(new MenuItemModel
            {
                Label = textInfo.ToTitleCase(styleName),
                Tag = $"switch-icon-style:{styleName}",
                Checked = string.Equals(currentStyle, styleName, StringComparison.OrdinalIgnoreCase),
            });
        }

        // Installed graphics packs (after separator)
        if (state.InstalledGraphicsPacks.Count > 0)
        {
            children.Add(new MenuItemModel { Type = MenuItemType.Separator });

            foreach (var pack in state.InstalledGraphicsPacks)
            {
                var packStyle = $"pack:{pack}";
                children.Add(new MenuItemModel
                {
                    Label = pack,
                    Tag = $"switch-icon-style:{packStyle}",
                    Checked = string.Equals(currentStyle, packStyle, StringComparison.OrdinalIgnoreCase),
                });
            }
        }

        return new MenuItemModel
        {
            Label = "Icon Style",
            Type = MenuItemType.Submenu,
            Children = children,
        };
    }

    private static MenuItemModel BuildEnabledPacksSubmenu(ControllerMenuState state)
    {
        var children = new List<MenuItemModel>();
        var disabled = state.Config.Sound.DisabledPacks;

        if (state.InstalledPacks.Count == 0)
        {
            children.Add(new MenuItemModel
            {
                Label = "(none installed)",
                Enabled = false,
            });
        }
        else
        {
            foreach (var pack in state.InstalledPacks)
            {
                var isDisabled = disabled.Any(d =>
                    string.Equals(d, pack, StringComparison.OrdinalIgnoreCase));
                children.Add(new MenuItemModel
                {
                    Label = pack,
                    Tag = $"toggle-pack-enabled:{pack}",
                    Checked = !isDisabled,
                });
            }
        }

        return new MenuItemModel
        {
            Label = "Enabled Packs",
            Type = MenuItemType.Submenu,
            Children = children,
        };
    }
}
