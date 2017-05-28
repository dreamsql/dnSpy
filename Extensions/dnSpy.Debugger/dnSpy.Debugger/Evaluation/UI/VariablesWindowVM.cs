﻿/*
    Copyright (C) 2014-2017 de4dot@gmail.com

    This file is part of dnSpy

    dnSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.ComponentModel.Composition;
using System.Diagnostics;
using dnSpy.Contracts.App;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.CallStack;
using dnSpy.Contracts.Debugger.Code;
using dnSpy.Contracts.Debugger.Evaluation;
using dnSpy.Contracts.TreeView;
using dnSpy.Debugger.Evaluation.ViewModel;
using dnSpy.Debugger.ToolWindows;
using dnSpy.Debugger.UI;

namespace dnSpy.Debugger.Evaluation.UI {
	abstract class VariablesWindowVMFactory {
		public abstract IVariablesWindowVM Create(VariablesWindowVMOptions variablesWindowVMOptions);
	}

	[Export(typeof(VariablesWindowVMFactory))]
	sealed class VariablesWindowVMFactoryImpl : VariablesWindowVMFactory {
		readonly Lazy<DbgManager> dbgManager;
		readonly UIDispatcher uiDispatcher;
		readonly Lazy<ValueNodesVMFactory> valueNodesVMFactory;
		readonly Lazy<DbgLanguageService> dbgLanguageService;
		readonly Lazy<DbgCallStackService> dbgCallStackService;
		readonly Lazy<IMessageBoxService> messageBoxService;

		[ImportingConstructor]
		VariablesWindowVMFactoryImpl(Lazy<DbgManager> dbgManager, UIDispatcher uiDispatcher, Lazy<ValueNodesVMFactory> valueNodesVMFactory, Lazy<DbgLanguageService> dbgLanguageService, Lazy<DbgCallStackService> dbgCallStackService, Lazy<IMessageBoxService> messageBoxService) {
			this.dbgManager = dbgManager;
			this.uiDispatcher = uiDispatcher;
			this.valueNodesVMFactory = valueNodesVMFactory;
			this.dbgLanguageService = dbgLanguageService;
			this.dbgCallStackService = dbgCallStackService;
			this.messageBoxService = messageBoxService;
		}

		public override IVariablesWindowVM Create(VariablesWindowVMOptions variablesWindowVMOptions) {
			uiDispatcher.VerifyAccess();
			return new VariablesWindowVM(variablesWindowVMOptions, dbgManager, uiDispatcher, valueNodesVMFactory, dbgLanguageService, dbgCallStackService, messageBoxService);
		}
	}

	interface IVariablesWindowVM {
		bool IsOpen { get; set; }
		bool IsVisible { get; set; }
		event EventHandler TreeViewChanged;
		ITreeView TreeView { get; }
		IValueNodesVM VM { get; }
	}

	sealed class VariablesWindowVM : IVariablesWindowVM, ILazyToolWindowVM {
		public bool IsOpen {
			get => lazyToolWindowVMHelper.IsOpen;
			set => lazyToolWindowVMHelper.IsOpen = value;
		}

		public bool IsVisible {
			get => lazyToolWindowVMHelper.IsVisible;
			set => lazyToolWindowVMHelper.IsVisible = value;
		}

		public event EventHandler TreeViewChanged;
		public ITreeView TreeView => valueNodesVM.TreeView;

		sealed class ValueNodesProviderImpl : ValueNodesProvider {
			public override event EventHandler NodesChanged;
			public override event EventHandler IsReadOnlyChanged;
			public override bool IsReadOnly => isReadOnly;
			public override event EventHandler LanguageChanged;
			public override DbgLanguage Language => language;
			bool isReadOnly;
			bool isOpen;
			DbgLanguage language;
			EvalContextInfo evalContextInfo;

			sealed class EvalContextInfo {
				public DbgEvaluationContext Context;
				public DbgLanguage Language;
				public DbgStackFrame Frame;

				public void Clear() {
					Context = null;
					Language = null;
					Frame = null;
				}
			}

			readonly VariablesWindowValueNodesProvider variablesWindowValueNodesProvider;
			readonly UIDispatcher uiDispatcher;
			readonly Lazy<DbgManager> dbgManager;
			readonly Lazy<DbgLanguageService> dbgLanguageService;
			readonly Lazy<DbgCallStackService> dbgCallStackService;

			public ValueNodesProviderImpl(VariablesWindowValueNodesProvider variablesWindowValueNodesProvider, UIDispatcher uiDispatcher, Lazy<DbgManager> dbgManager, Lazy<DbgLanguageService> dbgLanguageService, Lazy<DbgCallStackService> dbgCallStackService) {
				this.variablesWindowValueNodesProvider = variablesWindowValueNodesProvider ?? throw new ArgumentNullException(nameof(variablesWindowValueNodesProvider));
				this.uiDispatcher = uiDispatcher ?? throw new ArgumentNullException(nameof(uiDispatcher));
				this.dbgManager = dbgManager ?? throw new ArgumentNullException(nameof(dbgManager));
				this.dbgLanguageService = dbgLanguageService ?? throw new ArgumentNullException(nameof(dbgLanguageService));
				this.dbgCallStackService = dbgCallStackService ?? throw new ArgumentNullException(nameof(dbgCallStackService));
				evalContextInfo = new EvalContextInfo();
			}

			void UI(Action callback) => uiDispatcher.UI(callback);

			void DbgThread(Action callback) =>
				dbgManager.Value.Dispatcher.BeginInvoke(callback);

			public void Initialize_UI(bool enable) {
				uiDispatcher.VerifyAccess();
				isOpen = enable;
				evalContextInfo.Context?.Process.DbgManager.Close(evalContextInfo.Context);
				evalContextInfo.Clear();
				variablesWindowValueNodesProvider.Initialize(enable);
				if (enable)
					variablesWindowValueNodesProvider.NodesChanged += VariablesWindowValueNodesProvider_NodesChanged;
				else
					variablesWindowValueNodesProvider.NodesChanged -= VariablesWindowValueNodesProvider_NodesChanged;
				RefreshNodes_UI();
				DbgThread(() => InitializeDebugger_DbgThread(enable));
			}

			void InitializeDebugger_DbgThread(bool enable) {
				dbgManager.Value.Dispatcher.VerifyAccess();
				if (enable) {
					dbgLanguageService.Value.LanguageChanged += DbgLanguageService_LanguageChanged;
					dbgCallStackService.Value.FramesChanged += DbgCallStackService_FramesChanged;
				}
				else {
					dbgLanguageService.Value.LanguageChanged -= DbgLanguageService_LanguageChanged;
					dbgCallStackService.Value.FramesChanged -= DbgCallStackService_FramesChanged;
				}
			}

			void DbgLanguageService_LanguageChanged(object sender, DbgLanguageChangedEventArgs e) {
				var thread = dbgManager.Value.CurrentThread.Current;
				if (thread == null || thread.Runtime.Guid != e.RuntimeGuid)
					return;
				UI(() => RefreshNodes_UI());
			}

			void DbgCallStackService_FramesChanged(object sender, FramesChangedEventArgs e) =>
				UI(() => RefreshNodes_UI());

			void VariablesWindowValueNodesProvider_NodesChanged(object sender, EventArgs e) =>
				UI(() => RefreshNodes_UI());

			void RefreshNodes_UI() {
				uiDispatcher.VerifyAccess();
				var info = TryGetLanguage();
				if (info.language != language) {
					language = info.language;
					LanguageChanged?.Invoke(this, EventArgs.Empty);
				}
				bool newIsReadOnly = info.frame == null;
				NodesChanged?.Invoke(this, EventArgs.Empty);
				SetIsReadOnly_UI(newIsReadOnly);
			}

			(DbgLanguage language, DbgStackFrame frame) TryGetLanguage() {
				if (!isOpen)
					return (null, null);
				var frame = dbgCallStackService.Value.ActiveFrame;
				if (frame == null)
					return (null, null);
				var language = dbgLanguageService.Value.GetCurrentLanguage(frame.Thread.Runtime.Guid);
				return (language, frame);
			}

			public override DbgValueNodeInfo[] GetNodes() {
				uiDispatcher.VerifyAccess();
				var info = TryGetLanguage();
				if (info.frame == null)
					return variablesWindowValueNodesProvider.GetDefaultNodes();
				var evalContext = TryGetEvaluationContext();
				if (evalContext == null)
					return variablesWindowValueNodesProvider.GetDefaultNodes();
				var options = DbgEvaluationOptions.Expression;
				return variablesWindowValueNodesProvider.GetNodes(evalContext, info.language, info.frame, options);
			}

			void SetIsReadOnly_UI(bool newIsReadOnly) {
				uiDispatcher.VerifyAccess();
				if (isReadOnly == newIsReadOnly)
					return;
				isReadOnly = newIsReadOnly;
				IsReadOnlyChanged?.Invoke(this, EventArgs.Empty);
			}

			public override bool CanAddRemoveExpressions => variablesWindowValueNodesProvider.CanAddRemoveExpressions;

			public override void DeleteExpressions(string[] ids) {
				if (!CanAddRemoveExpressions)
					throw new InvalidOperationException();
				variablesWindowValueNodesProvider.DeleteExpressions(ids);
			}

			public override void ClearAllExpressions() {
				if (!CanAddRemoveExpressions)
					throw new InvalidOperationException();
				variablesWindowValueNodesProvider.ClearAllExpressions();
			}

			public override void EditExpression(string id, string expression) {
				if (!CanAddRemoveExpressions)
					throw new InvalidOperationException();
				variablesWindowValueNodesProvider.EditExpression(id, expression);
			}

			public override void AddExpressions(string[] expressions) {
				if (!CanAddRemoveExpressions)
					throw new InvalidOperationException();
				variablesWindowValueNodesProvider.AddExpressions(expressions);
			}

			public override DbgEvaluationContext TryGetEvaluationContext() {
				var info = TryGetLanguage();
				if (evalContextInfo.Language == info.language && evalContextInfo.Frame == info.frame)
					return evalContextInfo.Context;

				evalContextInfo.Context?.Process.DbgManager.Close(evalContextInfo.Context);
				evalContextInfo.Language = info.language;
				evalContextInfo.Frame = info.frame;
				if (info.frame != null)
					evalContextInfo.Context = info.language.CreateContext(info.frame.Runtime, info.frame.Location, EvaluationConstants.DefaultFuncEvalTimeout, DbgEvaluationContextOptions.None);
				else
					evalContextInfo.Context = null;
				return evalContextInfo.Context;
			}

			public override DbgStackFrame TryGetFrame() => TryGetLanguage().frame;
		}

		IValueNodesVM IVariablesWindowVM.VM => valueNodesVM;

		readonly VariablesWindowVMOptions variablesWindowVMOptions;
		readonly Lazy<DbgManager> dbgManager;
		readonly UIDispatcher uiDispatcher;
		readonly LazyToolWindowVMHelper lazyToolWindowVMHelper;
		readonly ValueNodesProviderImpl valueNodesProvider;
		readonly Lazy<ValueNodesVMFactory> valueNodesVMFactory;
		readonly Lazy<IMessageBoxService> messageBoxService;
		IValueNodesVM valueNodesVM;

		public VariablesWindowVM(VariablesWindowVMOptions variablesWindowVMOptions, Lazy<DbgManager> dbgManager, UIDispatcher uiDispatcher, Lazy<ValueNodesVMFactory> valueNodesVMFactory, Lazy<DbgLanguageService> dbgLanguageService, Lazy<DbgCallStackService> dbgCallStackService, Lazy<IMessageBoxService> messageBoxService) {
			uiDispatcher.VerifyAccess();
			this.variablesWindowVMOptions = variablesWindowVMOptions;
			this.dbgManager = dbgManager;
			this.uiDispatcher = uiDispatcher;
			lazyToolWindowVMHelper = new DebuggerLazyToolWindowVMHelper(this, uiDispatcher, dbgManager);
			valueNodesProvider = new ValueNodesProviderImpl(variablesWindowVMOptions.VariablesWindowValueNodesProvider, uiDispatcher, dbgManager, dbgLanguageService, dbgCallStackService);
			this.valueNodesVMFactory = valueNodesVMFactory;
			this.messageBoxService = messageBoxService;
		}

		// random thread
		void DbgThread(Action callback) =>
			dbgManager.Value.Dispatcher.BeginInvoke(callback);

		// random thread
		void UI(Action callback) => uiDispatcher.UI(callback);

		void ILazyToolWindowVM.Show() {
			uiDispatcher.VerifyAccess();
			InitializeDebugger_UI(enable: true);
		}

		void ILazyToolWindowVM.Hide() {
			uiDispatcher.VerifyAccess();
			InitializeDebugger_UI(enable: false);
		}

		void InitializeDebugger_UI(bool enable) {
			uiDispatcher.VerifyAccess();
			if (enable) {
				valueNodesProvider.Initialize_UI(enable);
				if (valueNodesVM == null) {
					var options = new ValueNodesVMOptions() {
						NodesProvider = valueNodesProvider,
						ShowMessageBox = ShowMessageBox,
						WindowContentType = variablesWindowVMOptions.WindowContentType,
						NameColumnName = variablesWindowVMOptions.NameColumnName,
						ValueColumnName = variablesWindowVMOptions.ValueColumnName,
						TypeColumnName = variablesWindowVMOptions.TypeColumnName,
						VariablesWindowKind = variablesWindowVMOptions.VariablesWindowKind,
						VariablesWindowGuid = variablesWindowVMOptions.VariablesWindowGuid,
					};
					valueNodesVM = valueNodesVMFactory.Value.Create(options);
				}
				valueNodesVM.Show();
				TreeViewChanged?.Invoke(this, EventArgs.Empty);
			}
			else {
				valueNodesVM?.Hide();
				TreeViewChanged?.Invoke(this, EventArgs.Empty);
				valueNodesProvider.Initialize_UI(enable);
			}
			DbgThread(() => InitializeDebugger_DbgThread(enable));
		}

		// DbgManager thread
		void InitializeDebugger_DbgThread(bool enable) {
			dbgManager.Value.Dispatcher.VerifyAccess();
			if (enable)
				dbgManager.Value.DelayedIsRunningChanged += DbgManager_DelayedIsRunningChanged;
			else
				dbgManager.Value.DelayedIsRunningChanged -= DbgManager_DelayedIsRunningChanged;
		}

		// DbgManager thread
		void DbgManager_DelayedIsRunningChanged(object sender, EventArgs e) {
			// If all processes are running and the window is hidden, hide it now
			if (!IsVisible)
				UI(() => lazyToolWindowVMHelper.TryHideWindow());
		}

		bool ShowMessageBox(string message, ShowMessageBoxButtons buttons) {
			MsgBoxButton mbb;
			MsgBoxButton resButton;
			switch (buttons) {
			case ShowMessageBoxButtons.YesNo:
				mbb = MsgBoxButton.Yes | MsgBoxButton.No;
				resButton = MsgBoxButton.Yes;
				break;
			case ShowMessageBoxButtons.OK:
				mbb = MsgBoxButton.OK;
				resButton = MsgBoxButton.OK;
				break;
			default:
				throw new ArgumentOutOfRangeException(nameof(buttons));
			}
			return messageBoxService.Value.Show(message, mbb) == resButton;
		}
	}
}
