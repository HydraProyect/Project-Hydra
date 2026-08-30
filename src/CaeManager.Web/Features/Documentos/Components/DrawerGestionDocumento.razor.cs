using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Documentos.Commands.CrearDocumento;
using CaeManager.Application.Documentos.Commands.RenovarDocumento;
using CaeManager.Application.Documentos.Queries.DetectarCamposDocumento;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentoPorId;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Application.Proyectos.Queries.ObtenerProyectosParaSelector;
using CaeManager.Application.TiposDocumento.Queries.ObtenerTiposDocumento;
using CaeManager.Application.Trabajadores.Commands.AsignarAliasTrabajador;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Application.Vehiculos.Queries.ObtenerVehiculosParaSelector;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Documentos;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Logging;

namespace CaeManager.Web.Features.Documentos.Components;

public partial class DrawerGestionDocumento : ComponentBase
{
    private const long TamanoMaximoArchivoBytes = 10 * 1024 * 1024;
    private const int MaximoArchivosPorSubida = 20;

    /// <summary>Se dispara tras crear o renovar con éxito — el host decide qué recargar (rejilla, acordeón…).</summary>
    [Parameter] public EventCallback OnGuardado { get; set; }

    private IReadOnlyList<TrabajadorSelectorDto> _trabajadoresDisponibles = [];
    private IReadOnlyList<ClienteSelectorDto> _clientesDisponibles = [];
    private IReadOnlyList<EmpresaSelectorDto> _empresasDisponibles = [];
    private IReadOnlyList<VehiculoSelectorDto> _vehiculosDisponibles = [];
    private IReadOnlyList<ProyectoSelectorDto> _proyectosDisponibles = [];
    private IReadOnlyList<TipoDocumentoListaDto> _tiposDisponibles = [];

    private bool _drawerVisible;
    private Guid? _editandoId;
    private Guid _versionEditando;
    private string _ambitoAplicacion = nameof(AmbitoAplicacion.Trabajador);
    private string _trabajadorId = string.Empty;
    private string _clienteId = string.Empty;
    private string _empresaId = string.Empty;
    private string _vehiculoId = string.Empty;
    private string _proyectoId = string.Empty;
    private string _propietarioNombreSoloLectura = string.Empty;
    private string _tipoDocumentoId = string.Empty;
    private string _tipoDocumentoNombreSoloLectura = string.Empty;
    private bool _tipoDocumentoAplicaVencimientoAutomaticoEdit;
    private string _fechaEmision = string.Empty;
    private DateOnly? _fechaEmisionOriginal;
    private string _fechaVencimientoManual = string.Empty;
    private string? _glosarioDescripcion;
    private string? _glosarioCriteriosValidacion;
    private string? _glosarioSeSolicitaA;
    private string? _glosarioObservaciones;
    private string? _archivoUrl;

    /// <summary>
    /// Clave de un archivo ya escrito en almacenamiento que todavía no tiene
    /// Documento que lo posea. El drawer guarda el blob al seleccionar el
    /// archivo, no al enviar el formulario, así que entre ambos momentos
    /// existe un archivo sin fila propietaria: cerrar el drawer, elegir otro
    /// archivo o abandonar el circuito lo dejaba en disco para siempre, sin
    /// nada que lo referenciara y por tanto fuera del alcance de la
    /// retención y de cualquier purga RGPD.
    ///
    /// Se distingue de <see cref="_archivoUrl"/> a propósito: al editar, ese
    /// campo lleva el archivo que el Documento YA tiene, y borrar ese sí
    /// sería destruir datos vivos. Solo se descarta lo que subió esta sesión
    /// del drawer y ningún comando llegó a adoptar.
    /// </summary>
    private string? _archivoUrlSubidoSinAdoptar;

    private bool _subiendoArchivo;
    private bool _detectandoCampos;
    private string? _aliasSugerido;
    private Guid? _trabajadorIdParaAliasSugerido;
    private bool _asignandoAliasSugerido;
    private bool _confirmarTipoSospechosoVisible;
    private string _tipoSospechosoDetectadoNombre = string.Empty;
    private string _tipoSospechosoSeleccionadoNombre = string.Empty;
    private string _comentarios = string.Empty;
    private bool _guardando;
    private string? _mensajeErrorFormulario;
    private Dictionary<string, string> _erroresCampo = new();

    private bool _confirmarVigenciaAnteriorVisible;
    private bool _procesandoConfirmacionVigencia;

    /// <summary>
    /// El alias (nombre con el que el trabajador firma o está dado de alta
    /// en plataformas externas) se incluye en el texto buscable para que
    /// escribirlo también lo encuentre — la identidad real para el
    /// pre-relleno automático siempre se verifica por DNI, nunca por este
    /// texto (ver DetectarCamposDocumentoQuery).
    /// </summary>
    private IReadOnlyList<OpcionBuscable> OpcionesTrabajadores => _trabajadoresDisponibles
        .Select(t => new OpcionBuscable(
            t.Id.ToString(),
            string.IsNullOrWhiteSpace(t.Alias) ? $"{t.NombreCompleto} ({t.Dni})" : $"{t.NombreCompleto} — {t.Alias} ({t.Dni})"))
        .ToList();

    /// <summary>
    /// Solo los tipos de documento sin vencimiento automático piden una
    /// fecha de vencimiento a mano — los automáticos la calculan siempre a
    /// partir de la vigencia en meses, así que no tiene sentido mostrarles
    /// el campo ni el botón de copiar.
    /// </summary>
    private bool RequiereVencimientoManual =>
        _editandoId is null
            ? _tiposDisponibles.FirstOrDefault(t => t.Id.ToString() == _tipoDocumentoId) is { AplicaVencimientoAutomatico: false }
            : !_tipoDocumentoAplicaVencimientoAutomaticoEdit;

    private bool TieneGlosario =>
        !string.IsNullOrWhiteSpace(_glosarioDescripcion)
        || !string.IsNullOrWhiteSpace(_glosarioCriteriosValidacion)
        || !string.IsNullOrWhiteSpace(_glosarioSeSolicitaA)
        || !string.IsNullOrWhiteSpace(_glosarioObservaciones);

    public async Task AbrirCrearAsync()
    {
        // Reabrir el drawer sin haber pasado por el cierre (lo hacen las
        // pantallas que lo abren directamente) no debe heredar el archivo de
        // la sesión anterior: se descarta antes de reiniciar el formulario.
        await DescartarArchivoSinAdoptarAsync();

        _ambitoAplicacion = nameof(AmbitoAplicacion.Trabajador);
        _trabajadoresDisponibles = await Mediator.Send(new ObtenerTrabajadoresParaSelectorQuery());
        _tiposDisponibles = await Mediator.Send(new ObtenerTiposDocumentoQuery(AmbitoAplicacion: AmbitoAplicacion.Trabajador));

        _editandoId = null;
        _trabajadorId = string.Empty;
        _clienteId = string.Empty;
        _empresaId = string.Empty;
        _vehiculoId = string.Empty;
        _proyectoId = string.Empty;
        _tipoDocumentoId = string.Empty;
        _fechaEmision = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
        _fechaEmisionOriginal = null;
        _fechaVencimientoManual = string.Empty;
        _archivoUrl = null;
        _comentarios = string.Empty;
        _glosarioDescripcion = null;
        _glosarioCriteriosValidacion = null;
        _glosarioSeSolicitaA = null;
        _glosarioObservaciones = null;
        _aliasSugerido = null;
        _trabajadorIdParaAliasSugerido = null;
        _confirmarTipoSospechosoVisible = false;
        _erroresCampo = new Dictionary<string, string>();
        _mensajeErrorFormulario = null;
        _drawerVisible = true;
        StateHasChanged();
    }

    /// <summary>
    /// Los cuatro "Abrir*" son la API pública que Documentos.razor y
    /// AcordeonAsignacionesCentro.razor invocan vía @ref (item 3 del backlog:
    /// gestionar in situ) — cada uno termina en StateHasChanged() explícito
    /// porque el render automático de Blazor tras un evento solo alcanza al
    /// componente que declaró el @onclick (el padre), nunca a este hijo cuyo
    /// estado interno se muta desde fuera. Sin esto el drawer no se abre: el
    /// campo cambia, pero nada le pide a ESTE componente que vuelva a pintar.
    /// </summary>
    public async Task AbrirCrearParaFaltanteAsync(Guid trabajadorId, Guid tipoDocumentoId)
    {
        await AbrirCrearAsync();
        _trabajadorId = trabajadorId.ToString();
        CambiarTipoDocumento(tipoDocumentoId.ToString());
        StateHasChanged();
    }

    public async Task AbrirCrearParaFaltanteEmpresaAsync(Guid empresaId, Guid tipoDocumentoId)
    {
        await AbrirCrearAsync();
        await CambiarAmbitoAsync(nameof(AmbitoAplicacion.Empresa));
        _empresaId = empresaId.ToString();
        CambiarTipoDocumento(tipoDocumentoId.ToString());
        StateHasChanged();
    }

    public async Task AbrirEditarAsync(Guid id)
    {
        // Mismo motivo que en AbrirCrearAsync — y aquí importa más, porque a
        // continuación _archivoUrl pasa a ser el archivo que el Documento ya
        // tiene, que no se debe descartar nunca.
        await DescartarArchivoSinAdoptarAsync();

        var documento = await Mediator.Send(new ObtenerDocumentoPorIdQuery(id));
        if (documento is null)
        {
            ToastService.Mostrar("No encontramos este documento. Puede que ya se haya eliminado.", TonoToast.Error);
            return;
        }

        _editandoId = documento.Id;
        _versionEditando = documento.Version;
        _ambitoAplicacion = documento.Ambito.ToString();
        _propietarioNombreSoloLectura = documento.PropietarioNombre;
        _tipoDocumentoNombreSoloLectura = documento.TipoDocumentoNombre;
        _tipoDocumentoAplicaVencimientoAutomaticoEdit = documento.TipoDocumentoAplicaVencimientoAutomatico;
        _fechaEmision = documento.FechaEmision.ToString("yyyy-MM-dd");
        _fechaEmisionOriginal = documento.FechaEmision;
        _fechaVencimientoManual = documento.FechaVencimiento?.ToString("yyyy-MM-dd") ?? string.Empty;
        _archivoUrl = documento.ArchivoUrl;
        _comentarios = documento.Comentarios ?? string.Empty;
        _glosarioDescripcion = documento.TipoDocumentoDescripcion;
        _glosarioCriteriosValidacion = documento.TipoDocumentoCriteriosValidacion;
        _glosarioSeSolicitaA = documento.TipoDocumentoSeSolicitaA;
        _glosarioObservaciones = documento.TipoDocumentoObservaciones;
        _erroresCampo = new Dictionary<string, string>();
        _mensajeErrorFormulario = null;
        _drawerVisible = true;
        StateHasChanged();
    }

    private async Task CambiarAmbitoAsync(string valor)
    {
        _ambitoAplicacion = valor;
        _trabajadorId = string.Empty;
        _clienteId = string.Empty;
        _empresaId = string.Empty;
        _vehiculoId = string.Empty;
        _proyectoId = string.Empty;
        _aliasSugerido = null;
        _trabajadorIdParaAliasSugerido = null;

        var ambito = Enum.Parse<AmbitoAplicacion>(valor);

        if (ambito == AmbitoAplicacion.Cliente && _clientesDisponibles.Count == 0)
            _clientesDisponibles = await Mediator.Send(new ObtenerClientesParaSelectorQuery());
        else if (ambito == AmbitoAplicacion.Empresa && _empresasDisponibles.Count == 0)
            _empresasDisponibles = await Mediator.Send(new ObtenerEmpresasParaSelectorQuery());
        else if (ambito == AmbitoAplicacion.Vehiculo && _vehiculosDisponibles.Count == 0)
            _vehiculosDisponibles = await Mediator.Send(new ObtenerVehiculosParaSelectorQuery());
        else if (ambito == AmbitoAplicacion.Proyecto && _proyectosDisponibles.Count == 0)
            _proyectosDisponibles = await Mediator.Send(new ObtenerProyectosParaSelectorQuery());

        _tiposDisponibles = await Mediator.Send(new ObtenerTiposDocumentoQuery(AmbitoAplicacion: ambito));
        CambiarTipoDocumento(string.Empty);
    }

    private void CambiarTipoDocumento(string valor)
    {
        _tipoDocumentoId = valor;

        var tipo = _tiposDisponibles.FirstOrDefault(t => t.Id.ToString() == valor);
        _glosarioDescripcion = tipo?.Descripcion;
        _glosarioCriteriosValidacion = tipo?.CriteriosValidacion;
        _glosarioSeSolicitaA = tipo?.SeSolicitaA;
        _glosarioObservaciones = tipo?.Observaciones;
    }

    private async Task CerrarDrawerAsync(bool visible)
    {
        // Cerrar sin guardar abandona el archivo que se hubiera subido ya.
        if (!visible)
            await DescartarArchivoSinAdoptarAsync();

        _drawerVisible = visible;
    }

    /// <summary>
    /// Borra el archivo subido que ningún comando llegó a adoptar. Es
    /// best-effort: si el borrado falla queda constancia del huérfano, pero
    /// no se interrumpe lo que el usuario estaba haciendo por ello.
    ///
    /// Compensa el fallo y el abandono, no la caída del proceso entre la
    /// escritura del blob y el commit: para eso hace falta staging durable
    /// con TTL y un recolector — decisión de arquitectura pendiente, ver el
    /// informe del Módulo 2.
    /// </summary>
    private async Task DescartarArchivoSinAdoptarAsync()
    {
        if (_archivoUrlSubidoSinAdoptar is not { } huerfano) return;

        _archivoUrlSubidoSinAdoptar = null;

        try
        {
            await AlmacenamientoArchivos.EliminarAsync(huerfano);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex,
                "Quedó un archivo huérfano en almacenamiento al descartar una subida: {ArchivoUrl}. " +
                "No tiene Documento propietario, así que la retención no lo alcanza.",
                huerfano);
        }
    }

    private void CopiarFechaEmisionAVencimiento() => _fechaVencimientoManual = _fechaEmision;

    /// <summary>Si el usuario cambia el trabajador a mano, la sugerencia de alias detectada para el anterior deja de tener sentido.</summary>
    private void CambiarTrabajadorSeleccionado(string valor)
    {
        _trabajadorId = valor;
        if (valor != _trabajadorIdParaAliasSugerido?.ToString())
            _aliasSugerido = null;
    }

    private async Task AceptarAliasSugeridoAsync()
    {
        if (_aliasSugerido is null || _trabajadorIdParaAliasSugerido is not { } trabajadorId
            || _trabajadorId != trabajadorId.ToString())
            return;

        _asignandoAliasSugerido = true;
        try
        {
            var resultado = await Mediator.Send(new AsignarAliasTrabajadorCommand(trabajadorId, _aliasSugerido));
            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return;
            }

            ToastService.Mostrar("Alias añadido al trabajador.", TonoToast.Exito);
            _aliasSugerido = null;
        }
        finally
        {
            _asignandoAliasSugerido = false;
        }
    }

    /// <summary>
    /// Acepta PDF, JPG, PNG y Word (.docx). Las imágenes y los Word se
    /// convierten a PDF automáticamente (Word vía LibreOffice headless, ver
    /// <see cref="ConversorArchivosPdf"/>); si se seleccionan varios archivos
    /// a la vez (p. ej. varias fotos de las páginas de un mismo documento),
    /// se combinan en un único PDF multipágina antes de guardarse — nunca se
    /// adjunta más de un archivo por Documento.
    /// </summary>
    private async Task ManejarArchivoSeleccionadoAsync(InputFileChangeEventArgs e)
    {
        var archivos = e.GetMultipleFiles(MaximoArchivosPorSubida);

        foreach (var archivo in archivos)
        {
            if (!ConversorArchivosPdf.EsPdf(archivo.Name)
                && !ConversorArchivosPdf.EsImagen(archivo.Name)
                && !ConversorArchivosPdf.EsWord(archivo.Name))
            {
                ToastService.Mostrar($"\"{archivo.Name}\" no es un PDF, JPG, PNG ni Word (.docx).", TonoToast.Error);
                return;
            }

            if (archivo.Size > TamanoMaximoArchivoBytes)
            {
                ToastService.Mostrar($"\"{archivo.Name}\" supera los 10 MB.", TonoToast.Error);
                return;
            }
        }

        _subiendoArchivo = true;
        StateHasChanged();

        try
        {
            var contenidos = new List<(byte[] Contenido, string NombreArchivo)>();
            foreach (var archivo in archivos)
            {
                await using var flujo = archivo.OpenReadStream(TamanoMaximoArchivoBytes);
                using var memoria = new MemoryStream();
                await flujo.CopyToAsync(memoria);
                contenidos.Add((memoria.ToArray(), archivo.Name));
            }

            // La comprobación de arriba solo mira el nombre — un archivo
            // renombrado a mano pasaría ese filtro. Esta mira los primeros
            // bytes del contenido real antes de convertirlo o guardarlo.
            foreach (var (contenido, nombreArchivo) in contenidos)
            {
                if (!ValidadorFirmaArchivo.TieneFirmaValida(contenido, nombreArchivo))
                {
                    ToastService.Mostrar(
                        $"\"{nombreArchivo}\" no es realmente un PDF, JPG, PNG ni Word (.docx) — su contenido no coincide con la extensión.",
                        TonoToast.Error);
                    return;
                }
            }

            var pdfUnificado = await ConversorArchivosPdf.UnificarAsync(contenidos, ConversorWordPdf);

            // Elegir un segundo archivo sustituye la clave del primero: si no
            // se descarta aquí, el primero se queda en almacenamiento sin que
            // nada vuelva a nombrarlo.
            await DescartarArchivoSinAdoptarAsync();

            using var flujoPdf = new MemoryStream(pdfUnificado);
            _archivoUrl = await AlmacenamientoArchivos.GuardarAsync(flujoPdf, "documento.pdf");
            _archivoUrlSubidoSinAdoptar = _archivoUrl;

            if (_editandoId is null && _ambitoAplicacion == nameof(AmbitoAplicacion.Trabajador))
                await DetectarCamposAsync(pdfUnificado);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Fallo al procesar el archivo subido en Documentos");
            ToastService.Mostrar("No pudimos procesar el archivo. Intenta nuevamente.", TonoToast.Error);
        }
        finally
        {
            _subiendoArchivo = false;
        }
    }

    /// <summary>
    /// Mejor esfuerzo, mismo criterio que la detección de trabajadores/
    /// verificación IA de <see cref="CrearDocumentoCommand"/>: si la IA no
    /// está disponible o no encuentra una sugerencia fiable, el alta
    /// manual sigue funcionando exactamente igual que antes — nunca se
    /// bloquea ni se fuerza la selección, solo se ahorra el trabajo cuando
    /// hay una coincidencia clara. El usuario conserva ambos campos
    /// editables y puede corregir la sugerencia antes de guardar.
    /// </summary>
    private async Task DetectarCamposAsync(byte[] contenidoPdf)
    {
        _detectandoCampos = true;
        StateHasChanged();

        try
        {
            var resultado = await Mediator.Send(
                new DetectarCamposDocumentoQuery(contenidoPdf, "documento.pdf", AmbitoAplicacion.Trabajador));

            if (resultado.EsFallido)
                return;

            var deteccion = resultado.Valor;
            var huboSugerencia = false;

            if (deteccion.TipoDocumentoId is { } tipoDetectadoId
                && _tiposDisponibles.FirstOrDefault(t => t.Id == tipoDetectadoId) is { } tipoDetectadoDto)
            {
                if (string.IsNullOrEmpty(_tipoDocumentoId))
                {
                    CambiarTipoDocumento(tipoDetectadoId.ToString());
                    huboSugerencia = true;
                }
                else if (_tipoDocumentoId != tipoDetectadoId.ToString())
                {
                    // El usuario ya había elegido un tipo antes de subir el
                    // archivo — no se lo pisamos en silencio (huboSugerencia
                    // del alias/trabajador sí puede seguir, esto es aparte).
                    _tipoSospechosoDetectadoNombre = tipoDetectadoDto.Nombre;
                    _tipoSospechosoSeleccionadoNombre = _tiposDisponibles.FirstOrDefault(t => t.Id.ToString() == _tipoDocumentoId)?.Nombre ?? "el tipo seleccionado";
                    _confirmarTipoSospechosoVisible = true;
                }
            }

            if (deteccion.TrabajadorId is { } trabajadorDetectadoId
                && _trabajadoresDisponibles.Any(t => t.Id == trabajadorDetectadoId))
            {
                _trabajadorId = trabajadorDetectadoId.ToString();
                huboSugerencia = true;

                if (deteccion.AliasSugerido is not null)
                {
                    _aliasSugerido = deteccion.AliasSugerido;
                    _trabajadorIdParaAliasSugerido = trabajadorDetectadoId;
                }
            }

            if (huboSugerencia)
                ToastService.Mostrar("Detectamos automáticamente el trabajador y/o el tipo de documento — revisa antes de guardar.", TonoToast.Info);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "No se pudo completar la detección automática de campos al subir un Documento.");
        }
        finally
        {
            _detectandoCampos = false;
        }
    }

    /// <summary>
    /// Al renovar, si la nueva fecha de emisión es anterior a la que ya
    /// tenía el documento probablemente se subió el archivo equivocado —
    /// se pide confirmación explícita en vez de guardar directamente.
    /// </summary>
    private async Task GuardarAsync()
    {
        if (_editandoId is not null
            && _fechaEmisionOriginal is not null
            && DateOnly.TryParse(_fechaEmision, out var nuevaFecha)
            && nuevaFecha < _fechaEmisionOriginal)
        {
            _confirmarVigenciaAnteriorVisible = true;
            return;
        }

        await GuardarInternoAsync();
    }

    private async Task ConfirmarGuardarConVigenciaAnteriorAsync()
    {
        _procesandoConfirmacionVigencia = true;
        try
        {
            await GuardarInternoAsync();
        }
        finally
        {
            _procesandoConfirmacionVigencia = false;
            _confirmarVigenciaAnteriorVisible = false;
        }
    }

    private async Task GuardarInternoAsync()
    {
        _guardando = true;
        _mensajeErrorFormulario = null;
        _erroresCampo = new Dictionary<string, string>();

        try
        {
            if (!DateOnly.TryParse(_fechaEmision, out var fechaEmision))
            {
                _mensajeErrorFormulario = "Introduce una fecha de emisión válida.";
                return;
            }

            DateOnly? fechaVencimientoManual = RequiereVencimientoManual && DateOnly.TryParse(_fechaVencimientoManual, out var fv)
                ? fv
                : null;

            var comentarios = string.IsNullOrWhiteSpace(_comentarios) ? null : _comentarios;
            string? mensajeError;

            if (_editandoId is null)
            {
                var ambito = Enum.Parse<AmbitoAplicacion>(_ambitoAplicacion);
                var propietarioId = ambito switch
                {
                    AmbitoAplicacion.Trabajador => _trabajadorId,
                    AmbitoAplicacion.Cliente => _clienteId,
                    AmbitoAplicacion.Vehiculo => _vehiculoId,
                    AmbitoAplicacion.Proyecto => _proyectoId,
                    _ => _empresaId
                };

                if (!Guid.TryParse(propietarioId, out var idPropietario))
                {
                    _mensajeErrorFormulario = ambito switch
                    {
                        AmbitoAplicacion.Trabajador => "Selecciona un trabajador.",
                        AmbitoAplicacion.Cliente => "Selecciona un cliente.",
                        AmbitoAplicacion.Vehiculo => "Selecciona un vehículo.",
                        AmbitoAplicacion.Proyecto => "Selecciona un proyecto.",
                        _ => "Selecciona una empresa."
                    };
                    return;
                }

                if (!Guid.TryParse(_tipoDocumentoId, out var tipoDocumentoId))
                {
                    _mensajeErrorFormulario = "Selecciona un tipo de documento.";
                    return;
                }

                var resultado = await Mediator.Send(new CrearDocumentoCommand(
                    TrabajadorId: ambito == AmbitoAplicacion.Trabajador ? idPropietario : null,
                    ClienteId: ambito == AmbitoAplicacion.Cliente ? idPropietario : null,
                    EmpresaId: ambito == AmbitoAplicacion.Empresa ? idPropietario : null,
                    VehiculoId: ambito == AmbitoAplicacion.Vehiculo ? idPropietario : null,
                    ProyectoId: ambito == AmbitoAplicacion.Proyecto ? idPropietario : null,
                    TipoDocumentoId: tipoDocumentoId,
                    FechaEmision: fechaEmision,
                    FechaVencimientoManual: fechaVencimientoManual,
                    ArchivoUrl: _archivoUrl,
                    Comentarios: comentarios));
                mensajeError = resultado.EsFallido ? resultado.Error.Mensaje : null;
            }
            else
            {
                var resultado = await Mediator.Send(
                    new RenovarDocumentoCommand(_editandoId.Value, fechaEmision, fechaVencimientoManual, _archivoUrl, comentarios, _versionEditando));
                mensajeError = resultado.EsFallido ? resultado.Error.Mensaje : null;
            }

            if (mensajeError is not null)
            {
                _mensajeErrorFormulario = mensajeError;
                return;
            }

            // El comando confirmó: el archivo ya tiene Documento propietario y
            // deja de ser candidato a descarte.
            _archivoUrlSubidoSinAdoptar = null;

            ToastService.Mostrar(
                _editandoId is null ? "Documento creado correctamente." : "Documento renovado correctamente.",
                TonoToast.Exito);

            _drawerVisible = false;
            if (OnGuardado.HasDelegate)
                await OnGuardado.InvokeAsync();
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
}
