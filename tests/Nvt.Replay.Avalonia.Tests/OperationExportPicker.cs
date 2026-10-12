// Copyright (c) 2026 Dennis Liu. All rights reserved.

using System.Reflection;
using System.Reflection.Emit;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Platform.Storage;

namespace Nvt.Replay.Avalonia.Tests;

// Avalonia 12 omits the locator API from its reference assemblies and seals storage
// interface marker methods. Implement only the public contracts through inherited
// methods, leaving those default markers intact. No private members are inspected.
// The gated report is cancelled before any writer uses these synthetic paths.
public class OperationExportPicker : IStorageProviderFactory, IDisposable
{
    private OperationExportPicker? _owner; // UI thread only; the factory owns all picker items.
    private IStorageFile? _file; // UI thread only.
    private IStorageFolder? _folder; // UI thread only.

    internal IDisposable Install()
    {
        var locatorType = typeof(AvaloniaLocator);
        var scope = (IDisposable)locatorType.GetMethod("EnterScope")!.Invoke(null, null)!;
        dynamic locator = locatorType.GetProperty("CurrentMutable")!.GetValue(null)!;
        locator.Bind<IStorageProviderFactory>().ToConstant(this);
        return scope;
    }

    public IStorageProvider CreateProvider(TopLevel topLevel)
    {
        var provider = (IStorageProvider)CreateStorageProxy(typeof(IStorageProvider));
        ((OperationExportPicker)provider)._owner = this;
        return provider;
    }

    private static object CreateStorageProxy(Type contract)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("U2StoragePicker"), AssemblyBuilderAccess.RunAndCollect);
        var type = assembly.DefineDynamicModule("U2StoragePicker").DefineType(
            "U2StoragePicker", TypeAttributes.Public, typeof(OperationExportPicker));
        type.AddInterfaceImplementation(contract);
        return Activator.CreateInstance(type.CreateType()!)!;
    }

    public void Dispose()
    {
        _file?.Dispose();
        _folder?.Dispose();
        GC.SuppressFinalize(this);
    }

    public virtual bool CanOpen => false;
    public virtual bool CanSave => true;
    public virtual bool CanPickFolder => true;
    public virtual string Name => "unused-u2-export";
    public virtual Uri Path { get; } = new(System.IO.Path.Combine(AppContext.BaseDirectory, "unused-u2-export"));
    public virtual bool CanBookmark => false;
    public virtual Task<IStorageFile?> SaveFilePickerAsync(FilePickerSaveOptions options) =>
        Task.FromResult<IStorageFile?>(_owner!._file ??= (IStorageFile)CreateStorageProxy(typeof(IStorageFile)));
    public virtual Task<IReadOnlyList<IStorageFolder>> OpenFolderPickerAsync(FolderPickerOpenOptions options) =>
        Task.FromResult<IReadOnlyList<IStorageFolder>>([_owner!._folder ??= (IStorageFolder)CreateStorageProxy(typeof(IStorageFolder))]);
    public virtual Task<IReadOnlyList<IStorageFile>> OpenFilePickerAsync(FilePickerOpenOptions options) => throw new NotSupportedException();
    public virtual Task<OpenFilePickerResult> OpenFilePickerWithResultAsync(FilePickerOpenOptions options) => throw new NotSupportedException();
    public virtual Task<SaveFilePickerResult> SaveFilePickerWithResultAsync(FilePickerSaveOptions options) => throw new NotSupportedException();
    public virtual Task<IStorageBookmarkFile?> OpenFileBookmarkAsync(string bookmark) => throw new NotSupportedException();
    public virtual Task<IStorageBookmarkFolder?> OpenFolderBookmarkAsync(string bookmark) => throw new NotSupportedException();
    public virtual Task<IStorageFile?> TryGetFileFromPathAsync(Uri filePath) => throw new NotSupportedException();
    public virtual Task<IStorageFolder?> TryGetFolderFromPathAsync(Uri folderPath) => throw new NotSupportedException();
    public virtual Task<IStorageFolder?> TryGetWellKnownFolderAsync(WellKnownFolder wellKnownFolder) => throw new NotSupportedException();
    public virtual Task<StorageItemProperties> GetBasicPropertiesAsync() => throw new NotSupportedException();
    public virtual Task<string?> SaveBookmarkAsync() => throw new NotSupportedException();
    public virtual Task<IStorageFolder?> GetParentAsync() => throw new NotSupportedException();
    public virtual Task DeleteAsync() => throw new NotSupportedException();
    public virtual Task<IStorageItem?> MoveAsync(IStorageFolder destination) => throw new NotSupportedException();
    public virtual Task<Stream> OpenReadAsync() => throw new NotSupportedException();
    public virtual Task<Stream> OpenWriteAsync() => throw new NotSupportedException();
    public virtual IAsyncEnumerable<IStorageItem> GetItemsAsync() => throw new NotSupportedException();
    public virtual Task<IStorageFolder?> GetFolderAsync(string name) => throw new NotSupportedException();
    public virtual Task<IStorageFile?> GetFileAsync(string name) => throw new NotSupportedException();
    public virtual Task<IStorageFile?> CreateFileAsync(string name) => throw new NotSupportedException();
    public virtual Task<IStorageFolder?> CreateFolderAsync(string name) => throw new NotSupportedException();
}
