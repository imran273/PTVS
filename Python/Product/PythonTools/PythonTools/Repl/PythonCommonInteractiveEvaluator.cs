﻿// Python Tools for Visual Studio
// Copyright(c) Microsoft Corporation
// All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the License); you may not use
// this file except in compliance with the License. You may obtain a copy of the
// License at http://www.apache.org/licenses/LICENSE-2.0
//
// THIS CODE IS PROVIDED ON AN  *AS IS* BASIS, WITHOUT WARRANTIES OR CONDITIONS
// OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION ANY
// IMPLIED WARRANTIES OR CONDITIONS OF TITLE, FITNESS FOR A PARTICULAR PURPOSE,
// MERCHANTABILITY OR NON-INFRINGEMENT.
//
// See the Apache Version 2.0 License for specific language governing
// permissions and limitations under the License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.PythonTools.Editor;
using Microsoft.PythonTools.Infrastructure;
using Microsoft.PythonTools.Intellisense;
using Microsoft.PythonTools.Interpreter;
using Microsoft.PythonTools.Options;
using Microsoft.PythonTools.Parsing;
using Microsoft.PythonTools.Project;
using Microsoft.VisualStudio.InteractiveWindow;
using Microsoft.VisualStudio.InteractiveWindow.Commands;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor.OptionsExtensionMethods;
using Microsoft.VisualStudio.Utilities;
using Microsoft.VisualStudioTools;
using Task = System.Threading.Tasks.Task;

namespace Microsoft.PythonTools.Repl {
    [InteractiveWindowRole("Execution")]
    [InteractiveWindowRole("Reset")]
    [ContentType(PythonCoreConstants.ContentType)]
    [ContentType(PredefinedInteractiveCommandsContentTypes.InteractiveCommandContentTypeName)]
    internal abstract class PythonCommonInteractiveEvaluator :
        IInteractiveEvaluator,
        IPythonInteractiveEvaluator,
        IMultipleScopeEvaluator,
        IPythonInteractiveIntellisense,
        IDisposable {
        protected readonly IServiceProvider _serviceProvider;
        private readonly StringBuilder _deferredOutput;

        private PythonProjectNode _projectWithHookedEvents;

        protected IInteractiveWindowCommands _commands;
        private IInteractiveWindow _window;
        private PythonInteractiveOptions _options;

        protected VsProjectAnalyzer _analyzer;
        private Uri _documentUri;
        private int _nextDocumentIndex;

        private bool _enableMultipleScopes;
        private IReadOnlyList<string> _availableScopes;

        private bool _isDisposed;

        internal const string DoNotResetConfigurationLaunchOption = "DoNotResetConfiguration";

        public PythonCommonInteractiveEvaluator(IServiceProvider serviceProvider) {
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _deferredOutput = new StringBuilder();
            _documentUri = new Uri($"repl://{Guid.NewGuid()}/repl.py");
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Usage", "CA2213:DisposableFieldsShouldBeDisposed", MessageId = "_analyzer")]
        protected virtual void Dispose(bool disposing) {
            if (_isDisposed) {
                return;
            }
            _isDisposed = true;

            if (_projectWithHookedEvents != null) {
                _projectWithHookedEvents.ActiveInterpreterChanged -= Project_ConfigurationChanged;
                _projectWithHookedEvents._searchPaths.Changed -= Project_ConfigurationChanged;
                _projectWithHookedEvents = null;
            }

            if (disposing) {
                _analyzer?.Dispose();
            }
        }

        public void Dispose() {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~PythonCommonInteractiveEvaluator() {
            Dispose(false);
        }

        public string DisplayName { get; set; }
        public string ProjectMoniker { get; set; }
        public LaunchConfiguration Configuration { get; set; }
        public string ScriptsPath { get; set; }

        public PythonLanguageVersion LanguageVersion {
            get {
                return Configuration?.Interpreter?.Version.ToLanguageVersion() ?? PythonLanguageVersion.None;
            }
        }

        public bool UseSmartHistoryKeys { get; set; }
        public bool LiveCompletionsOnly { get; set; }
        public string BackendName { get; set; }

        internal bool AssociatedProjectHasChanged { get; set; }

        private PythonProjectNode GetAssociatedPythonProject(InterpreterConfiguration interpreter = null) {
            _serviceProvider.GetUIThread().MustBeCalledFromUIThread();

            var moniker = ProjectMoniker;
            if (interpreter == null) {
                interpreter = Configuration?.Interpreter;
            }

            if (string.IsNullOrEmpty(moniker) && interpreter != null) {
                var interpreterService = _serviceProvider.GetComponentModel().GetService<IInterpreterRegistryService>();
                moniker = interpreterService.GetProperty(interpreter.Id, "ProjectMoniker") as string;
            }

            if (string.IsNullOrEmpty(moniker)) {
                return null;
            }

            return _serviceProvider.GetProjectFromFile(moniker);
        }


        public virtual VsProjectAnalyzer Analyzer => _analyzer;

        public virtual async Task<VsProjectAnalyzer> GetAnalyzerAsync() {
            if (_analyzer != null) {
                return _analyzer;
            }

            var config = Configuration;
            IPythonInterpreterFactory factory = null;
            if (config?.Interpreter != null) {
                var interpreterService = _serviceProvider.GetComponentModel().GetService<IInterpreterRegistryService>();
                factory = interpreterService.FindInterpreter(config.Interpreter.Id);
            }

            return await _serviceProvider.GetUIThread().InvokeTask(async () => {
                var a = _analyzer;
                if (a != null) {
                    return a;
                }
                if (factory == null) {
                    a = await _serviceProvider.GetPythonToolsService().GetSharedAnalyzerAsync();
                } else {
                    var pyProject = GetAssociatedPythonProject(config.Interpreter);

                    a = await VsProjectAnalyzer.CreateForInteractiveAsync(
                        _serviceProvider.GetComponentModel().GetService<PythonEditorServices>(),
                        factory,
                        DisplayName.IfNullOrEmpty("Unnamed")
                    );

                    IEnumerable<string> sp;
                    if (pyProject != null) {
                        sp = pyProject.GetSearchPaths();
                    } else {
                        var sln = _serviceProvider.GetService(typeof(SVsSolution)) as IVsSolution;
                        sp = sln?.EnumerateLoadedPythonProjects().SelectMany(p => p.GetSearchPaths()).ToArray();
                    }
                    await a.SetSearchPathsAsync(sp.MaybeEnumerate());
                }
                if (_analyzer != null) {
                    a.Dispose();
                } else {
                    _analyzer = a;
                }
                return _analyzer;
            });
        }

        public virtual Uri DocumentUri { get => _documentUri; protected set => _documentUri = value; }
        public virtual Uri NextDocumentUri() {
            var d = DocumentUri;
            if (d != null) {
                return new Uri(d, $"#{++_nextDocumentIndex}");
            }
            return null;
        }

        internal void WriteOutput(string text, bool addNewline = true) {
            var wnd = CurrentWindow;
            if (wnd == null) {
                lock (_deferredOutput) {
                    _deferredOutput.Append(text);
                }
            } else {
                AppendTextWithEscapes(wnd, text, addNewline, isError: false);
            }
        }

        internal void WriteError(string text, bool addNewline = true) {
            var wnd = CurrentWindow;
            if (wnd == null) {
                lock (_deferredOutput) {
                    _deferredOutput.Append(text);
                }
            } else {
                AppendTextWithEscapes(wnd, text, addNewline, isError: true);
            }
        }

        public abstract bool IsDisconnected { get; }

        public abstract bool IsExecuting { get; }

        public abstract string CurrentScopeName { get; }

        public abstract string CurrentScopePath { get; }

        public abstract string CurrentWorkingDirectory { get; }

        public IInteractiveWindow CurrentWindow {
            get {
                return _window;
            }
            set {
                if (_window != null) {
                }
                _commands = null;

                if (value != null) {
                    lock (_deferredOutput) {
                        AppendTextWithEscapes(value, _deferredOutput.ToString(), false, false);
                        _deferredOutput.Clear();
                    }

                    _options = _serviceProvider.GetPythonToolsService().InteractiveOptions;
                    _options.Changed += InteractiveOptions_Changed;
                    UseSmartHistoryKeys = _options.UseSmartHistory;
                    LiveCompletionsOnly = _options.LiveCompletionsOnly;
                } else {
                    if (_options != null) {
                        _options.Changed -= InteractiveOptions_Changed;
                        _options = null;
                    }
                }
                _window = value;
            }
        }

        private async void InteractiveOptions_Changed(object sender, EventArgs e) {
            if (!ReferenceEquals(sender, _options)) {
                return;
            }

            UseSmartHistoryKeys = _options.UseSmartHistory;
            LiveCompletionsOnly = _options.LiveCompletionsOnly;

            var window = CurrentWindow;
            if (window == null) {
                return;
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            window.TextView.Options.SetOptionValue(InteractiveWindowOptions.SmartUpDown, UseSmartHistoryKeys);
        }

        public bool EnableMultipleScopes {
            get { return _enableMultipleScopes; }
            set {
                if (_enableMultipleScopes != value) {
                    _enableMultipleScopes = value;
                    MultipleScopeSupportChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public event EventHandler<EventArgs> AvailableScopesChanged;
        public event EventHandler<EventArgs> MultipleScopeSupportChanged;

        public abstract Task<bool> GetSupportsMultipleStatementsAsync();

        protected void SetAvailableScopes(string[] scopes) {
            _availableScopes = scopes;
            AvailableScopesChanged?.Invoke(this, EventArgs.Empty);
        }

        public abstract IEnumerable<KeyValuePair<string, string>> GetAvailableScopesAndPaths();

        public abstract CompletionResult[] GetMemberNames(string text);

        public abstract OverloadDoc[] GetSignatureDocumentation(string text);

        public abstract void AbortExecution();

        public bool CanExecuteCode(string text) {
            return CanExecuteCode(text, out _);
        }

        protected bool CanExecuteCode(string text, out ParseResult pr) {
            pr = ParseResult.Complete;
            if (string.IsNullOrEmpty(text)) {
                return true;
            }
            if (string.IsNullOrWhiteSpace(text) && text.EndsWithOrdinal("\n")) {
                pr = ParseResult.Empty;
                return true;
            }

            var config = Configuration;
            var parser = Parser.CreateParser(new StringReader(text), LanguageVersion);
            parser.ParseInteractiveCode(out pr);
            if (pr == ParseResult.IncompleteStatement || pr == ParseResult.Empty) {
                return text.EndsWithOrdinal("\n");
            }
            if (pr == ParseResult.IncompleteToken) {
                return false;
            }
            return true;
        }

        protected abstract Task ExecuteStartupScripts(string scriptsPath);

        internal Task<ExecutionResult> UpdatePropertiesFromProjectMonikerAsync() {
            return _serviceProvider.GetUIThread().InvokeAsync(UpdatePropertiesFromProjectMoniker);
        }

        internal ExecutionResult UpdatePropertiesFromProjectMoniker() {
            try {
                if (_projectWithHookedEvents != null) {
                    _projectWithHookedEvents.ActiveInterpreterChanged -= Project_ConfigurationChanged;
                    _projectWithHookedEvents._searchPaths.Changed -= Project_ConfigurationChanged;
                    _projectWithHookedEvents = null;
                }

                AssociatedProjectHasChanged = false;
                var pyProj = GetAssociatedPythonProject();
                if (pyProj == null) {
                    return ExecutionResult.Success;
                }

                if (Configuration?.GetLaunchOption(DoNotResetConfigurationLaunchOption) == null) {
                    Configuration = pyProj.GetLaunchConfigurationOrThrow();
                    if (Configuration?.Interpreter != null) {
                        try {
                            ScriptsPath = GetScriptsPath(_serviceProvider, Configuration.Interpreter.Description, Configuration.Interpreter);
                        } catch (Exception ex) when (!ex.IsCriticalException()) {
                            ScriptsPath = null;
                        }
                    }
                }

                _projectWithHookedEvents = pyProj;
                pyProj.ActiveInterpreterChanged += Project_ConfigurationChanged;
                pyProj._searchPaths.Changed += Project_ConfigurationChanged;

                return ExecutionResult.Success;
            } catch (NoInterpretersException) {
                WriteError(Strings.NoInterpretersAvailable);
            } catch (MissingInterpreterException ex) {
                WriteError(ex.ToString());
            } catch (IOException ex) {
                WriteError(ex.ToString());
            } catch (Exception ex) when (!ex.IsCriticalException()) {
                WriteError(ex.ToUnhandledExceptionMessage(GetType()));
            }
            return ExecutionResult.Failure;
        }

        private void Project_ConfigurationChanged(object sender, EventArgs e) {
            var pyProj = _projectWithHookedEvents;
            _projectWithHookedEvents = null;

            if (pyProj != null) {
                Debug.Assert(pyProj == sender || pyProj._searchPaths == sender, "Unexpected project raised the event");
                // Only warn once
                pyProj.ActiveInterpreterChanged -= Project_ConfigurationChanged;
                pyProj._searchPaths.Changed -= Project_ConfigurationChanged;
                WriteError(Strings.ReplProjectConfigurationChanged.FormatUI(pyProj.Caption));
                AssociatedProjectHasChanged = true;
            }
        }

        internal static string GetScriptsPath(
            IServiceProvider provider,
            string displayName,
            InterpreterConfiguration config,
            bool onlyIfExists = true
        ) {
            provider.MustBeCalledFromUIThread();

            var root = provider.GetPythonToolsService().InteractiveOptions.Scripts;
            if (Path.GetInvalidPathChars().Any(c => root.Contains(c))) {
                throw new DirectoryNotFoundException(root);
            }

            if (string.IsNullOrEmpty(root)) {
                try {
                    if (!provider.TryGetShellProperty((__VSSPROPID)__VSSPROPID2.VSSPROPID_VisualStudioDir, out root)) {
                        root = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                        root = PathUtils.GetAbsoluteDirectoryPath(root, "Visual Studio {0}".FormatInvariant(AssemblyVersionInfo.VSName));
                    }

                    root = PathUtils.GetAbsoluteDirectoryPath(root, "Python Scripts");
                } catch (ArgumentException argEx) {
                    throw new DirectoryNotFoundException(root, argEx);
                }
            }

            string candidate;
            if (!string.IsNullOrEmpty(displayName)) {
                foreach (var c in Path.GetInvalidFileNameChars()) {
                    displayName = displayName.Replace(c, '_');
                }

                try {
                    candidate = PathUtils.GetAbsoluteDirectoryPath(root, displayName);
                } catch (ArgumentException argEx) {
                    throw new DirectoryNotFoundException(root, argEx);
                }
                if (!onlyIfExists || Directory.Exists(candidate)) {
                    return candidate;
                }
            }

            var version = config?.Version?.ToString();
            if (!string.IsNullOrEmpty(version)) {
                try {
                    candidate = PathUtils.GetAbsoluteDirectoryPath(root, version);
                } catch (ArgumentException argEx) {
                    throw new DirectoryNotFoundException(root, argEx);
                }
                if (!onlyIfExists || Directory.Exists(candidate)) {
                    return candidate;
                }
            }

            return null;
        }

        public abstract Task<ExecutionResult> ExecuteCodeAsync(string text);

        public abstract Task<bool> ExecuteFileAsync(string filename, string extraArgs);

        public abstract Task<bool> ExecuteModuleAsync(string name, string extraArgs);

        public abstract Task<bool> ExecuteProcessAsync(string filename, string extraArgs);

        const string _splitRegexPattern = @"(?x)\s*,\s*(?=(?:[^""]*""[^""]*"")*[^""]*$)"; // http://regexhero.net/library/52/
        private static Regex _splitLineRegex = new Regex(_splitRegexPattern);

        public string FormatClipboard() {
            // WPF and Windows Forms Clipboard behavior differs when it comes
            // to DataFormats.CommaSeparatedValue.
            // WPF will always return the data as a string, no matter how it
            // was set, but Windows Forms may return a Stream or a string.
            // Use WPF Clipboard fully qualified name to ensure we don't
            // accidentally end up using the wrong clipboard implementation
            // if this code is moved.
            if (System.Windows.Clipboard.ContainsData(System.Windows.DataFormats.CommaSeparatedValue)) {
                string data = System.Windows.Clipboard.GetData(System.Windows.DataFormats.CommaSeparatedValue) as string;
                if (data != null) {
                    string[] lines = data.Split(new[] { "\n", "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
                    StringBuilder res = new StringBuilder();
                    res.AppendLine("[");
                    foreach (var line in lines) {
                        string[] items = _splitLineRegex.Split(line);

                        res.Append("  [");
                        for (int i = 0; i < items.Length; i++) {
                            res.Append(FormatItem(items[i]));

                            if (i != items.Length - 1) {
                                res.Append(", ");
                            }
                        }
                        res.AppendLine("],");
                    }
                    res.AppendLine("]");
                    return res.ToString();
                }
            }

            var txt = System.Windows.Clipboard.GetText();
            if (!_serviceProvider.GetPythonToolsService().AdvancedOptions.PasteRemovesReplPrompts) {
                return txt;
            }


            return ReplPromptHelpers.RemovePrompts(
                txt,
                _window.TextView.Options.GetNewLineCharacter()
            );
        }

        private static string FormatItem(string item) {
            if (String.IsNullOrWhiteSpace(item)) {
                return "None";
            }
            double doubleVal;
            int intVal;
            if (Double.TryParse(item, out doubleVal) ||
                Int32.TryParse(item, out intVal)) {
                return item;
            }

            if (item[0] == '"' && item[item.Length - 1] == '"' && item.IndexOf(',') != -1) {
                // remove outer quotes, remove "" escaping
                item = item.Substring(1, item.Length - 2).Replace("\"\"", "\"");
            }

            // put in single quotes and escape single quotes and backslashes
            return "'" + item.Replace("\\", "\\\\").Replace("'", "\\'") + "'";
        }

        public IEnumerable<string> GetAvailableScopes() {
            return _availableScopes ?? Enumerable.Empty<string>();
        }

        public abstract void SetScope(string scopeName);

        public string GetPrompt() {
            if ((_window?.CurrentLanguageBuffer.CurrentSnapshot.LineCount ?? 1) > 1) {
                return SecondaryPrompt;
            } else {
                return PrimaryPrompt;
            }
        }

        internal abstract string PrimaryPrompt { get; }
        internal abstract string SecondaryPrompt { get; }

        public virtual async Task<ExecutionResult> InitializeAsync() {
            if (_commands != null) {
                // Already initialized
                return ExecutionResult.Success;
            }

            var msg = Strings.ReplInitializationMessage.FormatUI(
                DisplayName,
                AssemblyVersionInfo.Version,
                AssemblyVersionInfo.VSVersion
            ).Replace("&#x1b;", "\x1b");

            WriteOutput(msg, addNewline: true);

            var langBuffer = _window.CurrentLanguageBuffer;
            if (langBuffer != null) {
                // Reinitializing, and our new language buffer does not automatically
                // get connected to the Intellisense controller. Let's fix that.
                var controller = IntellisenseControllerProvider.GetController(_window.TextView);
                controller?.ConnectSubjectBuffer(langBuffer);
            }

            _window.TextView.Options.SetOptionValue(InteractiveWindowOptions.SmartUpDown, UseSmartHistoryKeys);
            _commands = GetInteractiveCommands(_serviceProvider, _window, this);

            return ExecutionResult.Success;
        }

        public async Task<ExecutionResult> ResetAsync(bool initialize = true) {
            await UpdatePropertiesFromProjectMonikerAsync();
            return await ResetWorkerAsync(initialize, false);
        }

        public async Task<ExecutionResult> ResetAsync(bool initialize, bool quiet) {
            await UpdatePropertiesFromProjectMonikerAsync();
            return await ResetWorkerAsync(initialize, quiet);
        }

        protected abstract Task<ExecutionResult> ResetWorkerAsync(bool initialize, bool quiet);

        internal Task InvokeAsync(Action action) {
            return _window.TextView.VisualElement.Dispatcher.InvokeAsync(action).Task;
        }

        internal void WriteFrameworkElement(System.Windows.UIElement control, System.Windows.Size desiredSize) {
            if (_window == null) {
                return;
            }

            _window.Write("");
            _window.FlushOutput();

            var caretPos = _window.TextView.Caret.Position.BufferPosition;
            var manager = InlineReplAdornmentProvider.GetManager(_window.TextView);
            manager.AddAdornment(new ZoomableInlineAdornment(control, _window.TextView, desiredSize), caretPos);
        }


        internal static IInteractiveWindowCommands GetInteractiveCommands(
            IServiceProvider serviceProvider,
            IInteractiveWindow window,
            IInteractiveEvaluator eval
        ) {
            var model = serviceProvider.GetComponentModel();
            var cmdFactory = model.GetService<IInteractiveWindowCommandsFactory>();
            var cmds = model.GetExtensions<IInteractiveWindowCommand>();
            var roles = eval.GetType()
                .GetCustomAttributes(typeof(InteractiveWindowRoleAttribute), true)
                .Select(r => ((InteractiveWindowRoleAttribute)r).Name)
                .ToArray();

            var contentTypeRegistry = model.GetService<IContentTypeRegistryService>();
            var contentTypes = eval.GetType()
                .GetCustomAttributes(typeof(ContentTypeAttribute), true)
                .Select(r => contentTypeRegistry.GetContentType(((ContentTypeAttribute)r).ContentTypes))
                .ToArray();

            return cmdFactory.CreateInteractiveCommands(
                window,
                "$",
                cmds.Where(x => IsCommandApplicable(x, roles, contentTypes))
            );
        }

        private static bool IsCommandApplicable(
            IInteractiveWindowCommand command,
            string[] supportedRoles,
            IContentType[] supportedContentTypes
        ) {
            var commandRoles = command.GetType().GetCustomAttributes(typeof(InteractiveWindowRoleAttribute), true).Select(r => ((InteractiveWindowRoleAttribute)r).Name).ToArray();

            // Commands with no roles are always applicable.
            // If a command specifies roles and none apply, exclude it
            if (commandRoles.Any() && !commandRoles.Intersect(supportedRoles).Any()) {
                return false;
            }

            var commandContentTypes = command.GetType()
                .GetCustomAttributes(typeof(ContentTypeAttribute), true)
                .Select(a => ((ContentTypeAttribute)a).ContentTypes)
                .ToArray();

            // Commands with no content type are always applicable
            // If a commands specifies content types and none apply, exclude it
            if (commandContentTypes.Any() && !commandContentTypes.Any(cct => supportedContentTypes.Any(sct => sct.IsOfType(cct)))) {
                return false;
            }

            return true;
        }

        #region Append Text helpers

        private static void AppendTextWithEscapes(
            IInteractiveWindow window,
            string text,
            bool addNewLine,
            bool isError
        ) {
            int start = 0, escape = text.IndexOfOrdinal("\x1b[");
            var colors = window.OutputBuffer.Properties.GetOrCreateSingletonProperty(
                ReplOutputClassifier.ColorKey,
                () => new List<ColoredSpan>()
            );
            ConsoleColor? color = null;

            Span span;
            var write = isError ? (Func<string, Span>)window.WriteError : window.Write;
            int lastEscape = -1;

            while (escape > lastEscape) {
                lastEscape = escape;

                span = write(text.Substring(start, escape - start));
                if (span.Length > 0) {
                    colors.Add(new ColoredSpan(span, color));
                }

                start = escape + 2;
                color = GetColorFromEscape(text, ref start);
                Debug.Assert(start >= escape + 2);

                escape = text.IndexOfOrdinal("\x1b[", start);
                Debug.Assert(escape < 0 || escape > lastEscape);
            }

            var rest = text.Substring(start);
            if (addNewLine) {
                rest += Environment.NewLine;
            }

            span = write(rest);
            if (span.Length > 0) {
                colors.Add(new ColoredSpan(span, color));
            }
        }

        private static ConsoleColor Change(ConsoleColor? from, ConsoleColor to) {
            return ((from ?? ConsoleColor.Black) & ConsoleColor.DarkGray) | to;
        }

        private static ConsoleColor? GetColorFromEscape(string text, ref int start) {
            // http://en.wikipedia.org/wiki/ANSI_escape_code
            // process any ansi color sequences...
            ConsoleColor? color = null;
            List<int> codes = new List<int>();
            int? value = 0;

            while (start < text.Length) {
                if (text[start] >= '0' && text[start] <= '9') {
                    // continue parsing the integer...
                    if (value == null) {
                        value = 0;
                    }
                    value = 10 * value.Value + (text[start] - '0');
                } else if (text[start] == ';') {
                    if (value != null) {
                        codes.Add(value.Value);
                        value = null;
                    } else {
                        // CSI ; - invalid or CSI ### ;;, both invalid
                        break;
                    }
                } else if (text[start] == 'm') {
                    start += 1;
                    if (value != null) {
                        codes.Add(value.Value);
                    }

                    // parsed a valid code
                    if (codes.Count == 0) {
                        // reset
                        color = null;
                    } else {
                        for (int j = 0; j < codes.Count; j++) {
                            switch (codes[j]) {
                                case 0: color = ConsoleColor.White; break;
                                case 1: // bright/bold
                                    color |= ConsoleColor.DarkGray;
                                    break;
                                case 2: // faint

                                case 3: // italic
                                case 4: // single underline
                                    break;
                                case 5: // blink slow
                                case 6: // blink fast
                                    break;
                                case 7: // negative
                                case 8: // conceal
                                case 9: // crossed out
                                case 10: // primary font
                                case 11: // 11-19, n-th alternate font
                                    break;
                                case 21: // bright/bold off 
                                    color &= ~ConsoleColor.DarkGray;
                                    break;
                                case 22: // normal intensity
                                case 24: // underline off
                                    break;
                                case 25: // blink off
                                    break;
                                case 27: // image - postive
                                case 28: // reveal
                                case 29: // not crossed out
                                case 30: color = Change(color, ConsoleColor.Black); break;
                                case 31: color = Change(color, ConsoleColor.DarkRed); break;
                                case 32: color = Change(color, ConsoleColor.DarkGreen); break;
                                case 33: color = Change(color, ConsoleColor.DarkYellow); break;
                                case 34: color = Change(color, ConsoleColor.DarkBlue); break;
                                case 35: color = Change(color, ConsoleColor.DarkMagenta); break;
                                case 36: color = Change(color, ConsoleColor.DarkCyan); break;
                                case 37: color = Change(color, ConsoleColor.Gray); break;
                                case 38: // xterm 286 background color
                                case 39: // default text color
                                    color = null;
                                    break;
                                case 40: // background colors
                                case 41:
                                case 42:
                                case 43:
                                case 44:
                                case 45:
                                case 46:
                                case 47: break;
                                case 90: color = ConsoleColor.DarkGray; break;
                                case 91: color = ConsoleColor.Red; break;
                                case 92: color = ConsoleColor.Green; break;
                                case 93: color = ConsoleColor.Yellow; break;
                                case 94: color = ConsoleColor.Blue; break;
                                case 95: color = ConsoleColor.Magenta; break;
                                case 96: color = ConsoleColor.Cyan; break;
                                case 97: color = ConsoleColor.White; break;
                            }
                        }
                    }
                    break;
                } else {
                    // unknown char, invalid escape
                    break;
                }
                start += 1;
            }
            return color;
        }

        #endregion
    }

    internal static class PythonCommonInteractiveEvaluatorExtensions {
        public static PythonCommonInteractiveEvaluator GetPythonEvaluator(this IInteractiveWindow window) {
            var pie = window?.Evaluator as PythonCommonInteractiveEvaluator;
            if (pie != null) {
                return pie;
            }

            pie = (window?.Evaluator as SelectableReplEvaluator)?.Evaluator as PythonCommonInteractiveEvaluator;
            return pie;
        }

        public static async Task<bool> GetSupportsMultipleStatements(this IInteractiveWindow window) {
            var pie = window.GetPythonEvaluator();
            if (pie == null) {
                return false;
            }
            return await pie.GetSupportsMultipleStatementsAsync();
        }
    }
}
