﻿using System.Collections;
using System.Linq;
using System.Threading;
using CUE4Parse.FileProvider.Objects;
using FModel.Framework;
using FModel.Services;

namespace FModel.ViewModels.Commands;

public class RightClickMenuCommand : ViewModelCommand<ApplicationViewModel>
{
    private ThreadWorkerViewModel _threadWorkerView => ApplicationService.ThreadWorkerView;

    public RightClickMenuCommand(ApplicationViewModel contextViewModel) : base(contextViewModel)
    {
    }

    public override async void Execute(ApplicationViewModel contextViewModel, object parameter)
    {
        if (parameter is not object[] parameters || parameters[0] is not string trigger)
            return;

        var entries = ((IList) parameters[1]).Cast<GameFile>().ToArray();
        if (!entries.Any()) return;

        var updateUi = entries.Length > 1 ? EBulkType.Auto : EBulkType.None;
        await _threadWorkerView.Begin(cancellationToken =>
        {
            switch (trigger)
            {
                case "Assets_Extract_New_Tab":
                    foreach (var entry in entries)
                    {
                        Thread.Yield();
                        cancellationToken.ThrowIfCancellationRequested();
                        contextViewModel.CUE4Parse.Extract(cancellationToken, entry, true);
                    }
                    break;
                case "Assets_Show_Metadata":
                    foreach (var entry in entries)
                    {
                        Thread.Yield();
                        cancellationToken.ThrowIfCancellationRequested();
                        contextViewModel.CUE4Parse.ShowMetadata(entry);
                    }
                    break;
                case "Assets_Export_Data":
                    foreach (var entry in entries)
                    {
                        Thread.Yield();
                        cancellationToken.ThrowIfCancellationRequested();
                        contextViewModel.CUE4Parse.ExportData(entry);
                    }
                    break;
                case "Assets_Save_Properties":
                    foreach (var entry in entries)
                    {
                        Thread.Yield();
                        cancellationToken.ThrowIfCancellationRequested();
                        contextViewModel.CUE4Parse.Extract(cancellationToken, entry, false, EBulkType.Properties | updateUi);
                    }
                    break;
                case "Assets_Save_Textures":
                    foreach (var entry in entries)
                    {
                        Thread.Yield();
                        cancellationToken.ThrowIfCancellationRequested();
                        contextViewModel.CUE4Parse.Extract(cancellationToken, entry, false, EBulkType.Textures | updateUi);
                    }
                    break;
                case "Assets_Save_Models":
                    foreach (var entry in entries)
                    {
                        Thread.Yield();
                        cancellationToken.ThrowIfCancellationRequested();
                        contextViewModel.CUE4Parse.Extract(cancellationToken, entry, false, EBulkType.Meshes | updateUi);
                    }
                    break;
                case "Assets_Save_Animations":
                    foreach (var entry in entries)
                    {
                        Thread.Yield();
                        cancellationToken.ThrowIfCancellationRequested();
                        contextViewModel.CUE4Parse.Extract(cancellationToken, entry, false, EBulkType.Animations | updateUi);
                    }
                    break;
                case "Assets_View_Diff":
                    var entry1 = entries.FirstOrDefault();
                    if (entry1 != null)
                    {
                        contextViewModel.CUE4Parse.ShowAssetDiff(entry1.Path).GetAwaiter().GetResult();
                    }
                    break;
            }
        });
    }
}
