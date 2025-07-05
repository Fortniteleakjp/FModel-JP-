using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using FModel.Extensions;
using FModel.Services;
using FModel.Settings;
using ICSharpCode.AvalonEdit.Document;
using Newtonsoft.Json;

namespace FModel.Views.Resources.Controls;

public partial class EndpointEditor
{
    private readonly EEndpointType _type;
    private bool _isTested;

    public EndpointEditor(EndpointSettings endpoint, string title, EEndpointType type)
    {
        DataContext = endpoint;
        _type = type;
        _isTested = endpoint.IsValid;

        InitializeComponent();

        Title = title;
        TargetResponse.SyntaxHighlighting =
            EndpointResponse.SyntaxHighlighting = AvalonExtensions.HighlighterSelector("json");

        InstructionBox.Text = type switch
        {
            EEndpointType.Aes =>
@"この機能を使うには、あなたがAPIやjsonに詳しく知っており、独自のAPIを持っている必要があります。

知識がない場合は、
API : https://fortnitecentral.genxgames.gg/api/v1/aes
取得するもの : $.['mainKey','dynamicKeys']
を入力してください。",
            EEndpointType.Mapping =>
@"この機能を使うには、あなたがAPIやjsonに詳しく知っており、独自のAPIを持っている必要があります。

知識がない場合は、
API : https://fortnitecentral.genxgames.gg/api/v1/mappings
取得するもの : $.[0].['url','fileName']
を入力してください。",
            _ => ""
        };
    }

    private void OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = _isTested && DataContext is EndpointSettings { IsValid: true };
        Close();
    }

    private async void OnSend(object sender, RoutedEventArgs e)
    {
        if (DataContext is not EndpointSettings endpoint) return;

        var body = await ApplicationService.ApiEndpointView.DynamicApi.GetRequestBody(default, endpoint.Url).ConfigureAwait(false);
        Application.Current.Dispatcher.Invoke(delegate
        {
            EndpointResponse.Document ??= new TextDocument();
            EndpointResponse.Document.Text = body.ToString(Formatting.Indented);
        });
    }

    private void OnTest(object sender, RoutedEventArgs e)
    {
        if (DataContext is not EndpointSettings endpoint) return;

        endpoint.TryValidate(ApplicationService.ApiEndpointView.DynamicApi, _type, out var response);
        _isTested = true;

        TargetResponse.Document ??= new TextDocument();
        TargetResponse.Document.Text = JsonConvert.SerializeObject(response, Formatting.Indented);
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox { IsLoaded: true } ||
            DataContext is not EndpointSettings endpoint) return;
        endpoint.IsValid = false;
    }

    private void OnSyntax(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo { FileName = "https://support.smartbear.com/alertsite/docs/monitors/api/endpoint/jsonpath.html", UseShellExecute = true });
    }

    private void OnEvaluator(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo { FileName = "https://jsonpath.herokuapp.com/", UseShellExecute = true });
    }
}

