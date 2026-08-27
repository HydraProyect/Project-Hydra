using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Application.TiposDocumento.Commands.ActualizarDeteccionTrabajadoresGlobal;
using CaeManager.Application.TiposDocumento.Commands.ActualizarLecturaIaGlobal;
using CaeManager.Application.TiposDocumento.Commands.ActualizarPerfilDocumentoOficialGlobal;
using CaeManager.Application.TiposDocumento.Commands.ActualizarVerificacionIaGlobal;
using CaeManager.Application.TiposDocumento.Commands.CrearTipoDocumento;
using CaeManager.Application.TiposDocumento.Commands.EditarTipoDocumento;
using CaeManager.Application.TiposDocumento.Queries.ObtenerTipoDocumentoPorId;
using CaeManager.Application.TiposDocumento.Queries.ObtenerTiposDocumento;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;
using FluentValidation;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace CaeManager.Web.Features.TiposDocumento.Pages;

public partial class TiposDocumento : ComponentBase
{
    private int _tamanoPagina = 20;

    private IReadOnlyList<TipoDocumentoListaDto> _tipos = [];
    private bool _cargando = true;
    private bool _errorCarga;
    private int _pagina = 1;

    private IReadOnlyList<ClienteSelectorDto> _clientesFiltroDisponibles = [];
    private IReadOnlyList<EmpresaSelectorDto> _empresasFiltroDisponibles = [];
    private IReadOnlyList<CentroSelectorDto> _centrosFiltroDisponibles = [];
    private string _clienteFiltroId = string.Empty;
    private string _empresaFiltroId = string.Empty;
    private string _centroFiltroId = string.Empty;
    private string _terminoBusqueda = string.Empty;

    private IReadOnlyList<CentroSelectorDto> _centrosDisponibles = [];

    private bool _drawerVisible;
    private Guid? _editandoId;
    private string _nombre = string.Empty;
    private string _ambitoAplicacion = nameof(AmbitoAplicacion.Trabajador);
    private bool _aplicaVencimientoAutomatico;
    private RequisitoDocumental _requerido;
    private NaturalezaJuridica _naturaleza;
    private string _vigenciaMeses = string.Empty;
    private string _orden = "0";
    private string _notas = string.Empty;
    private string _descripcion = string.Empty;
    private string _criteriosValidacion = string.Empty;
    private string _seSolicitaA = string.Empty;
    private string _observaciones = string.Empty;
    private HashSet<Guid> _centroIdsSeleccionados = [];
    private string _aliasNuevo = string.Empty;
    private List<string> _aliasesSeleccionados = [];
    private bool _guardando;
    private string? _mensajeErrorFormulario;
    private Dictionary<string, string> _erroresCampo = new();

    private int TotalPaginas => Math.Max(1, (int)Math.Ceiling(_tipos.Count / (double)_tamanoPagina));
    private IReadOnlyList<TipoDocumentoListaDto> TiposDePagina => _tipos.Skip((_pagina - 1) * _tamanoPagina).Take(_tamanoPagina).ToList();

    protected override async Task OnInitializedAsync()
    {
        _clientesFiltroDisponibles = await Mediator.Send(new ObtenerClientesParaSelectorQuery());
        await CargarAsync();
    }

    private Task IrAPaginaAsync(int pagina)
    {
        _pagina = pagina;
        return Task.CompletedTask;
    }

    // H5 (docs/ux-audit/05-trabajadores-vehiculos.md): selector de tamaño de página, compartido por PaginadorSimple.razor.
    private Task CambiarTamanoPaginaAsync(int tamano)
    {
        _tamanoPagina = tamano;
        _pagina = 1;
        return Task.CompletedTask;
    }

    private async Task CargarAsync()
    {
        _cargando = true;
        _errorCarga = false;
        StateHasChanged();

        try
        {
            Guid? clienteId = Guid.TryParse(_clienteFiltroId, out var cId) ? cId : null;
            Guid? empresaId = Guid.TryParse(_empresaFiltroId, out var eId) ? eId : null;
            Guid? centroId = Guid.TryParse(_centroFiltroId, out var ceId) ? ceId : null;

            var texto = string.IsNullOrWhiteSpace(_terminoBusqueda) ? null : _terminoBusqueda;
            _tipos = await Mediator.Send(new ObtenerTiposDocumentoQuery(clienteId, empresaId, centroId, Texto: texto));
            _pagina = 1;
        }
        catch (Exception)
        {
            _errorCarga = true;
        }
        finally
        {
            _cargando = false;
        }
    }

    private async Task CambiarClienteFiltroAsync(string clienteId)
    {
        _clienteFiltroId = clienteId;
        _empresaFiltroId = string.Empty;
        _centroFiltroId = string.Empty;

        _empresasFiltroDisponibles = Guid.TryParse(clienteId, out var id)
            ? await Mediator.Send(new ObtenerEmpresasParaSelectorQuery(id))
            : [];
        _centrosFiltroDisponibles = [];

        await CargarAsync();
    }

    private async Task CambiarEmpresaFiltroAsync(string empresaId)
    {
        _empresaFiltroId = empresaId;
        _centroFiltroId = string.Empty;

        _centrosFiltroDisponibles = Guid.TryParse(empresaId, out var id) && Guid.TryParse(_clienteFiltroId, out var clienteId)
            ? await Mediator.Send(new ObtenerCentrosParaSelectorQuery(clienteId, id))
            : [];

        await CargarAsync();
    }

    private Task CambiarCentroFiltroAsync(string centroId)
    {
        _centroFiltroId = centroId;
        return CargarAsync();
    }

    // Busca por Nombre o por cualquiera de los alias (TC2, CIF...) — la razón
    // de ser del campo de alias: antes de que existiera, la única forma de
    // encontrar "TC2" era que estuviera escrito dentro del nombre.
    private Task BuscarAsync(string texto)
    {
        _terminoBusqueda = texto;
        return CargarAsync();
    }

    private async Task AbrirCrear()
    {
        _centrosDisponibles = await Mediator.Send(new ObtenerCentrosParaSelectorQuery());

        _editandoId = null;
        _nombre = string.Empty;
        _ambitoAplicacion = nameof(AmbitoAplicacion.Trabajador);
        _aplicaVencimientoAutomatico = false;
        _requerido = RequisitoDocumental.No;
        _naturaleza = NaturalezaJuridica.RequisitoCliente;
        _vigenciaMeses = string.Empty;
        _orden = (_tipos.Count > 0 ? _tipos.Max(t => t.Orden) + 1 : 1).ToString();
        _notas = string.Empty;
        _descripcion = string.Empty;
        _criteriosValidacion = string.Empty;
        _seSolicitaA = string.Empty;
        _observaciones = string.Empty;
        _centroIdsSeleccionados = [];
        _aliasNuevo = string.Empty;
        _aliasesSeleccionados = [];
        _erroresCampo = new Dictionary<string, string>();
        _mensajeErrorFormulario = null;
        _drawerVisible = true;
    }

    private async Task AbrirEditarAsync(Guid id)
    {
        _centrosDisponibles = await Mediator.Send(new ObtenerCentrosParaSelectorQuery());

        var tipo = await Mediator.Send(new ObtenerTipoDocumentoPorIdQuery(id));
        if (tipo is null)
        {
            ToastService.Mostrar("No encontramos este tipo de documento.", TonoToast.Error);
            await CargarAsync();
            return;
        }

        _editandoId = tipo.Id;
        _nombre = tipo.Nombre;
        _ambitoAplicacion = tipo.AmbitoAplicacion.ToString();
        _aplicaVencimientoAutomatico = tipo.AplicaVencimientoAutomatico;
        _requerido = tipo.Requerido;
        _naturaleza = tipo.Naturaleza;
        _vigenciaMeses = tipo.VigenciaMeses?.ToString() ?? string.Empty;
        _orden = tipo.Orden.ToString();
        _notas = tipo.Notas ?? string.Empty;
        _descripcion = tipo.Descripcion ?? string.Empty;
        _criteriosValidacion = tipo.CriteriosValidacion ?? string.Empty;
        _seSolicitaA = tipo.SeSolicitaA ?? string.Empty;
        _observaciones = tipo.Observaciones ?? string.Empty;
        _centroIdsSeleccionados = tipo.CentroIds.ToHashSet();
        _aliasNuevo = string.Empty;
        _aliasesSeleccionados = tipo.Aliases.ToList();
        _erroresCampo = new Dictionary<string, string>();
        _mensajeErrorFormulario = null;
        _drawerVisible = true;
    }

    private void AlternarCentro(Guid centroId, bool seleccionado)
    {
        if (seleccionado)
            _centroIdsSeleccionados.Add(centroId);
        else
            _centroIdsSeleccionados.Remove(centroId);
    }

    private void AgregarAlias()
    {
        var alias = _aliasNuevo.Trim();
        if (alias.Length == 0)
            return;

        if (!_aliasesSeleccionados.Contains(alias, StringComparer.OrdinalIgnoreCase))
            _aliasesSeleccionados.Add(alias);

        _aliasNuevo = string.Empty;
    }

    private void QuitarAlias(string alias) => _aliasesSeleccionados.Remove(alias);

    private Task ManejarTeclaAliasAsync(KeyboardEventArgs e)
    {
        if (e.Key == "Enter")
            AgregarAlias();

        return Task.CompletedTask;
    }

    private Task CerrarDrawerAsync(bool visible)
    {
        _drawerVisible = visible;
        return Task.CompletedTask;
    }

    private async Task GuardarAsync()
    {
        _guardando = true;
        _mensajeErrorFormulario = null;
        _erroresCampo = new Dictionary<string, string>();

        try
        {
            var vigenciaMeses = int.TryParse(_vigenciaMeses, out var v) ? v : (int?)null;
            var orden = int.TryParse(_orden, out var o) ? o : 0;
            var notas = string.IsNullOrWhiteSpace(_notas) ? null : _notas;
            var descripcion = string.IsNullOrWhiteSpace(_descripcion) ? null : _descripcion;
            var criteriosValidacion = string.IsNullOrWhiteSpace(_criteriosValidacion) ? null : _criteriosValidacion;
            var seSolicitaA = string.IsNullOrWhiteSpace(_seSolicitaA) ? null : _seSolicitaA;
            var observaciones = string.IsNullOrWhiteSpace(_observaciones) ? null : _observaciones;
            var centroIds = _centroIdsSeleccionados.ToList();

            string? mensajeError;

            if (_editandoId is null)
            {
                var ambito = Enum.Parse<AmbitoAplicacion>(_ambitoAplicacion);
                var resultado = await Mediator.Send(
                    new CrearTipoDocumentoCommand(
                        _nombre, vigenciaMeses, _aplicaVencimientoAutomatico, orden, ambito, _requerido, _naturaleza, notas,
                        descripcion, criteriosValidacion, seSolicitaA, observaciones, centroIds, _aliasesSeleccionados));
                mensajeError = resultado.EsFallido ? resultado.Error.Mensaje : null;
            }
            else
            {
                var resultado = await Mediator.Send(
                    new EditarTipoDocumentoCommand(
                        _editandoId.Value, _nombre, vigenciaMeses, _aplicaVencimientoAutomatico, orden, _requerido, _naturaleza, notas,
                        descripcion, criteriosValidacion, seSolicitaA, observaciones, centroIds, _aliasesSeleccionados));
                mensajeError = resultado.EsFallido ? resultado.Error.Mensaje : null;
            }

            if (mensajeError is not null)
            {
                _mensajeErrorFormulario = mensajeError;
                return;
            }

            ToastService.Mostrar(
                _editandoId is null ? "Tipo de documento creado correctamente." : "Tipo de documento actualizado correctamente.",
                TonoToast.Exito);

            _drawerVisible = false;
            await CargarAsync();
        }
        catch (ValidationException ex)
        {
            _erroresCampo = ex.Errors
                .GroupBy(e => e.PropertyName)
                .ToDictionary(g => g.Key, g => g.First().ErrorMessage);
        }
        catch (Exception)
        {
            _mensajeErrorFormulario = "No pudimos guardar los cambios. Intenta nuevamente en unos segundos.";
        }
        finally
        {
            _guardando = false;
        }
    }

    private string? ObtenerError(string campo) => _erroresCampo.GetValueOrDefault(campo);

    private async Task AlternarLecturaIaAsync(Guid tipoDocumentoId, bool activa)
    {
        var resultado = await Mediator.Send(new ActualizarLecturaIaGlobalCommand(tipoDocumentoId, activa));
        if (resultado.EsFallido)
        {
            ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
            return;
        }

        await CargarAsync();
    }

    private async Task AlternarDeteccionTrabajadoresAsync(Guid tipoDocumentoId, bool activa)
    {
        var resultado = await Mediator.Send(new ActualizarDeteccionTrabajadoresGlobalCommand(tipoDocumentoId, activa));
        if (resultado.EsFallido)
        {
            ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
            return;
        }

        await CargarAsync();
    }

    private async Task AlternarVerificacionIaAsync(Guid tipoDocumentoId, bool activa)
    {
        var resultado = await Mediator.Send(new ActualizarVerificacionIaGlobalCommand(tipoDocumentoId, activa));
        if (resultado.EsFallido)
        {
            ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
            return;
        }

        await CargarAsync();
    }

    private async Task CambiarPerfilDocumentoOficialAsync(Guid tipoDocumentoId, string? valorSeleccionado)
    {
        if (!Enum.TryParse<PerfilDocumentoOficial>(valorSeleccionado, out var perfil))
            return;

        var resultado = await Mediator.Send(new ActualizarPerfilDocumentoOficialGlobalCommand(tipoDocumentoId, perfil));
        if (resultado.EsFallido)
        {
            ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
            return;
        }

        await CargarAsync();
    }
}
