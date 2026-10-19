using CaeManager.Application.Documentos.Queries.ObtenerEntidadesAsociadasDeDocumentos;
using CaeManager.Domain.Documentos;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace CaeManager.Web.Features.Documentos.Components;

public partial class PastillaEntidadAsociadaDocumento
{
[Parameter] public Guid? Valor { get; set; }
    [Parameter] public AmbitoAplicacion? Ambito { get; set; }
    [Parameter] public string? Nombre { get; set; }
    [Parameter] public EventCallback<EntidadAsociadaDocumentoDto?> ValorChanged { get; set; }

    private static long _contador;
    private readonly string _panelId = $"entidad-documento-{System.Threading.Interlocked.Increment(ref _contador)}";
    private ElementReference _disparador, _panel;
    private bool _abierto, _cargando, _error, _desechado, _focoPanel, _focoDisparador;
    private string _busqueda = string.Empty;
    private int _pagina = 1, _totalPaginas, _version;
    private IReadOnlyList<EntidadAsociadaDocumentoDto> _elementos = [];
    private CancellationTokenSource? _carga;
    private bool HayFiltrosActivos => !string.IsNullOrWhiteSpace(_busqueda);

    private async Task AlternarAsync()
    {
        if (_abierto) { await CerrarAsync(); return; }
        _abierto = true;
        _focoPanel = true;
        _pagina = 1;
        await CargarAsync();
    }

    private async Task BuscarAsync(string busqueda)
    {
        _busqueda = busqueda;
        _pagina = 1;
        if (_abierto) await CargarAsync();
    }

    private async Task PaginarAsync(int diferencia)
    {
        _pagina = Math.Clamp(_pagina + diferencia, 1, Math.Max(1, _totalPaginas));
        await CargarAsync();
    }

    private async Task CargarAsync()
    {
        _carga?.Cancel();
        _carga?.Dispose();
        _carga = new CancellationTokenSource();
        var token = _carga.Token;
        var version = ++_version;
        _cargando = true;
        _error = false;
        try
        {
            var resultado = await Mediator.Send(new ObtenerEntidadesAsociadasDeDocumentosQuery(
                Busqueda: _busqueda, Pagina: _pagina, TamanoPagina: 20), token);
            if (_desechado || !_abierto || version != _version) return;
            _elementos = resultado.Elementos;
            _totalPaginas = resultado.TotalPaginas;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!_desechado && _abierto && version == _version) _error = true;
        }
        finally
        {
            if (!_desechado && version == _version) _cargando = false;
        }
    }

    private async Task ElegirAsync(EntidadAsociadaDocumentoDto? entidad)
    {
        await CerrarAsync();
        await ValorChanged.InvokeAsync(entidad);
    }

    private Task CerrarAsync()
    {
        _abierto = false;
        _focoDisparador = true;
        _focoPanel = false;
        ++_version;
        _carga?.Cancel();
        return Task.CompletedTask;
    }

    private Task TeclaAsync(KeyboardEventArgs e) => e.Key == "Escape" && _abierto ? CerrarAsync() : Task.CompletedTask;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_focoPanel && _abierto) { _focoPanel = false; await _panel.FocusAsync(); }
        if (_focoDisparador) { _focoDisparador = false; await _disparador.FocusAsync(); }
    }

    private string TextoAmbito(AmbitoAplicacion ambito) => Textos[$"Ambito{ambito}"].Value;

    public void Dispose()
    {
        _desechado = true;
        ++_version;
        _carga?.Cancel();
        _carga?.Dispose();
    }
}
