﻿// Copyright (c) 2024 Files Community
// Licensed under the MIT License. See the LICENSE.

using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Input;
using Windows.Storage.Pickers;
using Files.Shared.Helpers;

namespace Files.App.ViewModels.Dialogs
{
	public sealed class CreateShortcutDialogViewModel : ObservableObject
	{
		// User's working directory
		public readonly string WorkingDirectory;

		// Placeholder text of destination path textbox
		public readonly string DestinationPlaceholder = $@"{Constants.UserEnvironmentPaths.SystemDrivePath}\Users\";

		// Tells whether destination path exists
		public bool DestinationPathExists { get; set; }

		// Tells whether the shortcut has been created
		public bool ShortcutCreatedSuccessfully { get; private set; }

		// Shortcut name with extension
		public string ShortcutCompleteName { get; private set; } = string.Empty;

		// Full path of the destination item
		private string _fullPath;

		// Arguments to be passed to the destination item if it's an executable
		private string _arguments;

		// Previous path of the destination item
		private string _previousShortcutTargetPath;

		// Destination of the shortcut chosen by the user (can be a path, a command or a URL)
		private string _shortcutTarget;
		public string ShortcutTarget
		{
			get => _shortcutTarget;
			set
			{
				if (!SetProperty(ref _shortcutTarget, value))
					return;

				OnPropertyChanged(nameof(ShowWarningTip));
				if (string.IsNullOrWhiteSpace(ShortcutTarget))
				{
					DestinationPathExists = false;
					IsLocationValid = false;
					_previousShortcutTargetPath = string.Empty;
					return;
				}
				try
				{
					var trimmed = ShortcutTarget.Trim();
					// If the text starts with '"', try to parse the quoted part as path, and the rest as arguments
					if (trimmed.StartsWith('"'))
					{
						var endQuoteIndex = trimmed.IndexOf('"', 1);
						if (endQuoteIndex == -1)
						{
							DestinationPathExists = false;
							IsLocationValid = false;
							_previousShortcutTargetPath = string.Empty;
							return;
						}

						var quoted = trimmed[1..endQuoteIndex];

						if (quoted == _previousShortcutTargetPath)
						{
							_arguments = !Directory.Exists(_fullPath) ? trimmed[(endQuoteIndex + 1)..] : string.Empty;
							return;
						}

						if (Path.Exists(quoted) 
						    && Path.IsPathFullyQualified(quoted)
						    && quoted != Path.GetPathRoot(quoted))
						{
							DestinationPathExists = true;
							IsLocationValid = true;
							_fullPath = Path.GetFullPath(quoted);
							_arguments = !Directory.Exists(_fullPath) ? trimmed[(endQuoteIndex + 1)..] : string.Empty;
							_previousShortcutTargetPath = quoted;
							return;
						}

						// If the quoted part is a valid filename, try to find it in the PATH
						if (quoted == Path.GetFileName(quoted)
							&& quoted.IndexOfAny(Path.GetInvalidFileNameChars()) == -1
							&& PathHelpers.TryGetFullPath(quoted, out _fullPath)
							)
						{
							DestinationPathExists = true;
							IsLocationValid = true;
							_arguments = trimmed[(endQuoteIndex + 1)..];
							_previousShortcutTargetPath = quoted;
							return;
						}

						var uri = new Uri(quoted);
						DestinationPathExists = false;
						IsLocationValid = uri.IsWellFormedOriginalString();
						_fullPath = quoted;
						_arguments = string.Empty;
						_previousShortcutTargetPath = string.Empty;
					}
					else
					{
						if (trimmed == _previousShortcutTargetPath)
						{
							_arguments = trimmed.Split(' ')[1..].Aggregate(_arguments, (current, arg) => current + arg + " ");
							return;
						}

						// Try to parse the whole text as path
						if (Path.Exists(trimmed)
						    && Path.IsPathFullyQualified(trimmed)
							&& trimmed != Path.GetPathRoot(trimmed))
						{
							DestinationPathExists = true;
							IsLocationValid = true;
							_fullPath = Path.GetFullPath(trimmed);
							_arguments = string.Empty;
							_previousShortcutTargetPath = string.Empty;
							return;
						}

						var filename = trimmed.Split(' ')[0];
						if (filename == Path.GetFileName(filename)
							&& filename.IndexOfAny(Path.GetInvalidFileNameChars()) == -1
							&& PathHelpers.TryGetFullPath(filename, out _fullPath)
							)
						{
							DestinationPathExists = true;
							IsLocationValid = true;
							_arguments = trimmed.Split(' ')[1..].Aggregate(_arguments, (current, arg) => current + arg + " ");
							_previousShortcutTargetPath = filename;
							return;
						}

						var uri = new Uri(trimmed);
						DestinationPathExists = false;
						IsLocationValid = uri.IsWellFormedOriginalString();
						_fullPath = trimmed;
						_arguments = string.Empty;
						_previousShortcutTargetPath = string.Empty;
					}

				}
				catch (Exception)
				{
					DestinationPathExists = false;
					IsLocationValid = false;
					_fullPath = string.Empty;
					_arguments = string.Empty;
					_previousShortcutTargetPath = string.Empty;
				}
			}
		}

		// Tells if the selected destination is valid (Path exists or URL is well-formed). Used to enable primary button
		private bool _isLocationValid;
		public bool IsLocationValid
		{
			get => _isLocationValid;
			set
			{
				if (SetProperty(ref _isLocationValid, value))
					OnPropertyChanged(nameof(ShowWarningTip));
			}
		}

		public bool ShowWarningTip => !string.IsNullOrEmpty(ShortcutTarget) && !_isLocationValid;

		// Command invoked when the user clicks the 'Browse' button
		public ICommand SelectDestinationCommand { get; private set; }

		// Command invoked when the user clicks primary button
		public ICommand PrimaryButtonCommand { get; private set; }

		public CreateShortcutDialogViewModel(string workingDirectory)
		{
			WorkingDirectory = workingDirectory;
			_shortcutTarget = string.Empty;

			SelectDestinationCommand = new AsyncRelayCommand(SelectDestination);
			PrimaryButtonCommand = new AsyncRelayCommand(CreateShortcutAsync);
		}

		private Task SelectDestination()
		{
			Win32PInvoke.BROWSEINFO bi = new Win32PInvoke.BROWSEINFO();
			bi.ulFlags = 0x00004000;
			bi.lpszTitle = "Select a folder";
			nint pidl = Win32PInvoke.SHBrowseForFolder(ref bi);
			if (pidl != nint.Zero)
			{
				StringBuilder path = new StringBuilder(260);
				if (Win32PInvoke.SHGetPathFromIDList(pidl, path))
				{
					ShortcutTarget = path.ToString();
				}
				Marshal.FreeCoTaskMem(pidl);
			}

			return Task.CompletedTask;
		}

		private async Task CreateShortcutAsync()
		{
			string? destinationName;
			var extension = DestinationPathExists ? ".lnk" : ".url";

			if (DestinationPathExists)
			{
				destinationName = Path.GetFileName(_fullPath);

				if(string.IsNullOrEmpty(_fullPath))
				{
					
					var destinationPath = _fullPath.Replace('/', '\\');

					if (destinationPath.EndsWith('\\'))
						destinationPath = destinationPath.Substring(0, destinationPath.Length - 1);

					destinationName = destinationPath.Substring(destinationPath.LastIndexOf('\\') + 1);
				}
			}
			else
			{
				var uri = new Uri(_fullPath);
				destinationName = uri.Host;
			}

			var shortcutName = FilesystemHelpers.GetShortcutNamingPreference(destinationName);
			ShortcutCompleteName = shortcutName + extension;
			var filePath = Path.Combine(WorkingDirectory, ShortcutCompleteName);

			int fileNumber = 1;
			while (Path.Exists(filePath))
			{
				ShortcutCompleteName = shortcutName + $" ({++fileNumber})" + extension;
				filePath = Path.Combine(WorkingDirectory, ShortcutCompleteName);
			}

			ShortcutCreatedSuccessfully = await FileOperationsHelpers.CreateOrUpdateLinkAsync(filePath, _fullPath, _arguments);
		}
	}
}