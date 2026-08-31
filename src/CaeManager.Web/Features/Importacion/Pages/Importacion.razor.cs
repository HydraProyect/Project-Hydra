using CaeManager.Application.Common;
using CaeManager.Application.Importacion;
using CaeManager.Application.Importacion.Commands.EjecutarImportacion;
using CaeManager.Application.Importacion.Commands.EjecutarImportacionCombinada;
using CaeManager.Application.Importacion.Commands.RegistrarHistorialImportacion;
using CaeManager.Application.Importacion.Queries;
using CaeManager.Application.Importacion.Queries.ObtenerHistorialImportaciones;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Identity;

namespace CaeManager.Web.Features.Importacion.Pages;

public partial class Importacion : CaeManager.Web.Components.PaginaIntegrableConfiguracionBase
{
    private const long TamanoMaximoCaeCompletaBytes = 20 * 1024 * 1024;
    private const long TamanoMaximoPlantillaBytes = 5 * 1024 * 1024;

    private sealed record DefinicionPlantilla(
        string Id, string Icono, string Titulo, string Descripcion, string? RutaPlantillaBlanco, long TamanoMaximoBytes);

    /// <summary>
    /// Copy y orden fieles al array TEMPLATES del mockup real (Importar
    /// datos TALVEG.dc.html). "cae" es la única sin plantilla en blanco —
    /// decisión ya tomada en la Importacion.razor anterior a este wizard
    /// ("procesa un archivo ya existente", no una plantilla propia) y que
    /// este wizard no cambia, solo hereda.
    /// </summary>
    private static readonly IReadOnlyList<DefinicionPlantilla> Plantillas =
    [
        new("cae", "CAE", "Importación CAE completa (multi-hoja)",
            "Clientes, empresas, centros, trabajadores y sus documentos en un solo libro (Cuadro de Control CAE). Para arrancar una cartera entera.",
            null, TamanoMaximoCaeCompletaBytes),
        new("clientes", "CLI", "Plantilla de Clientes",
            "Solo clientes con sus datos fiscales y de contacto. No toca centros ni trabajadores existentes.",
            "/clientes/plantilla.xlsx", TamanoMaximoPlantillaBytes),
        new("combinada", "CMB", "Combinada: Cliente + Empresas + Centros + Trabajadores",
            "Estructura organizativa completa sin documentos. Útil al incorporar un cliente nuevo con su plantilla.",
            "/clientes/plantilla-combinada.xlsx", TamanoMaximoPlantillaBytes),
        new("documentos", "DOC", "Documentos",
            "Lote de documentos con propietario y tipo por fila. Los PDF se aportan después con subida múltiple.",
            "/documentos/plantilla.xlsx", TamanoMaximoPlantillaBytes)
    ];

    private static readonly IReadOnlyList<string> NombresPasos =
        ["Elegir plantilla", "Analizar", "Revisar plan", "Confirmar", "Reporte"];

    /// <summary>Fila unificada de plan, proyectada desde PlanImportacionDto o PlanImportacionCombinadaDto — ver ProyectarFilas.</summary>
    private sealed record FilaPlan(string Entidad, string Accion, TonoBadge Tono, string Motivo);

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ToastService ToastService { get; set; } = default!;
    [Inject] private UserManager<ApplicationUser> UserManager { get; set; } = default!;
    [Inject] private PuertaAccesoDatos PuertaAccesoDatos { get; set; } = default!;

    [SupplyParameterFromQuery(Name = "plantilla")]
    private string? PlantillaInicial { get; set; }

    private int _step = 1;
    private int _pasoMaximoAlcanzado = 1;
    private string _plantillaId = "cae";

    protected override void OnInitialized()
    {
        if (PlantillaInicial is not null && Plantillas.Any(p => p.Id == PlantillaInicial))
            _plantillaId = PlantillaInicial;
    }

    private string? _nombreArchivo;
    private bool _analizando;
    private string? _mensajeError;
    private PlanImportacionDto? _planSimple;
    private PlanImportacionCombinadaDto? _planCombinada;

    private bool _confirmado;
    private bool _reemplazarExistentes;
    private bool _importando;

    private ResultadoImportacionDto? _resultadoSimple;
    private ResultadoImportacionCombinadaDto? _resultadoCombinada;

    private bool _cargandoHistorial = true;
    private IReadOnlyList<HistorialImportacionDto> _historial = [];
    private readonly Dictionary<Guid, string> _usuariosPorId = [];

    private DefinicionPlantilla PlantillaActual => Plantillas.First(p => p.Id == _plantillaId);
    private static readonly string[] MensajesAnalizando =
        ["Leyendo el archivo…", "Comprobando duplicados…", "Validando DNI y CIF…", "Cruzando con la cartera…"];

    protected override Task OnInitializedAsync() => CargarHistorialAsync();

    private async Task CargarHistorialAsync()
    {
        _cargandoHistorial = true;
        StateHasChanged();

        try
        {
            _historial = await Mediator.Send(new ObtenerHistorialImportacionesQuery());

            var idsFaltantes = _historial.Select(h => h.EjecutadaPorUsuarioId).Distinct()
                .Where(id => !_usuariosPorId.ContainsKey(id)).ToList();

            // Mismo patrón que Auditoria.razor.cs: UserManager no pasa por
            // MediatR y compite por el mismo DbContext scoped que el resto
            // del layout.
            await PuertaAccesoDatos.EjecutarAsync(async () =>
            {
                foreach (var id in idsFaltantes)
                {
                    var usuario = await UserManager.FindByIdAsync(id.ToString());
                    _usuariosPorId[id] = usuario?.NombreCompleto ?? usuario?.Email ?? "(usuario eliminado)";
                }
            });
        }
        catch (Exception)
        {
            _historial = [];
        }
        finally
        {
            _cargandoHistorial = false;
        }
    }

    private string NombreUsuario(Guid id) => _usuariosPorId.GetValueOrDefault(id, "—");

    private void IrAPaso(int paso)
    {
        if (paso > _pasoMaximoAlcanzado) return;
        _step = paso;
    }

    private void SeleccionarPlantilla(string plantillaId) => _plantillaId = plantillaId;

    private void ContinuarAAnalizar()
    {
        _step = 2;
        _pasoMaximoAlcanzado = Math.Max(_pasoMaximoAlcanzado, 2);
    }

    private async Task ManejarArchivoSeleccionadoAsync(InputFileChangeEventArgs e)
    {
        var archivo = e.File;
        _mensajeError = null;
        _planSimple = null;
        _planCombinada = null;

        var plantilla = PlantillaActual;
        if (archivo.Size > plantilla.TamanoMaximoBytes)
        {
            ToastService.Mostrar($"El archivo no puede superar los {plantilla.TamanoMaximoBytes / (1024 * 1024)} MB.", TonoToast.Error);
            return;
        }

        _analizando = true;
        _nombreArchivo = archivo.Name;
        StateHasChanged();

        try
        {
            await using var flujo = archivo.OpenReadStream(plantilla.TamanoMaximoBytes);
            using var memoria = new MemoryStream();
            await flujo.CopyToAsync(memoria);
            var contenido = memoria.ToArray();

            switch (_plantillaId)
            {
                case "cae":
                    _planSimple = await Mediator.Send(new AnalizarImportacionExcelQuery(contenido));
                    break;
                case "clientes":
                    _planSimple = await Mediator.Send(new AnalizarPlantillaClientesQuery(contenido));
                    break;
                case "documentos":
                    _planSimple = await Mediator.Send(new AnalizarPlantillaDocumentosQuery(contenido));
                    break;
                case "combinada":
                    _planCombinada = await Mediator.Send(new AnalizarPlantillaCombinadaQuery(contenido));
                    break;
            }
        }
        catch (Exception)
        {
            _mensajeError = "No pudimos leer este archivo. Comprueba que sea el formato de importación de esta plantilla.";
        }
        finally
        {
            _analizando = false;
        }
    }

    private bool TienePlan => _planSimple is not null || _planCombinada is not null;

    private void ContinuarARevisarPlan()
    {
        if (!TienePlan) return;
        _step = 3;
        _pasoMaximoAlcanzado = Math.Max(_pasoMaximoAlcanzado, 3);
    }

    private int TotalACrear => _planSimple is { } ps
        ? ps.ClientesCentros.Count(c => !c.YaExisteCliente) + ps.ClientesCentros.Count(c => !c.YaExisteCentro)
            + ps.Empresas.Count(e => !e.YaExiste) + ps.Trabajadores.Count(t => !t.YaExiste)
            + ps.Documentos.Count(d => !d.YaExiste) + ps.Asignaciones.Count(a => !a.YaExiste)
        : _planCombinada is { } pc
            ? pc.Clientes.Count(c => !c.YaExiste) + pc.Empresas.Count(e => !e.YaExiste)
                + pc.Centros.Count(c => !c.YaExiste) + pc.Trabajadores.Count(t => !t.YaExiste)
            : 0;

    private int TotalAdvertencias => _planSimple?.Advertencias.Count ?? _planCombinada?.Advertencias.Count ?? 0;
    private int TotalOmitidos => _planSimple?.Omitidos.Count ?? _planCombinada?.Omitidos.Count ?? 0;

    /// <summary>
    /// Une las listas tipadas de cualquiera de los dos DTOs de plan en filas
    /// Crear/Aviso/Omitir homogéneas para la tabla del wizard — el mockup
    /// pide "Fila" (número de fila del Excel), pero eso solo lo llevan
    /// Advertencias/Omitidos hoy (ItemImportacionDto.Fila); las filas "Crear"
    /// no tienen un número de fila real que mostrar en el backend actual, así
    /// que no se inventa uno.
    /// </summary>
    private IReadOnlyList<FilaPlan> ProyectarFilas()
    {
        var filas = new List<FilaPlan>();

        if (_planSimple is { } ps)
        {
            filas.AddRange(ps.ClientesCentros.Where(c => !c.YaExisteCliente).Select(c => new FilaPlan(c.Nombre, "Crear cliente", TonoBadge.Exito, "Nombre nuevo en Centros_Plataformas.")));
            filas.AddRange(ps.ClientesCentros.Where(c => !c.YaExisteCentro).Select(c => new FilaPlan(c.Nombre, "Crear centro", TonoBadge.Exito, "Nombre nuevo en Centros_Plataformas.")));
            filas.AddRange(ps.Empresas.Where(e => !e.YaExiste).Select(e => new FilaPlan(e.RazonSocial, "Crear empresa", TonoBadge.Exito, "Razón social nueva.")));
            filas.AddRange(ps.Trabajadores.Where(t => !t.YaExiste).Select(t => new FilaPlan($"{t.Nombre} {t.Apellidos} ({t.Dni})", "Crear trabajador", TonoBadge.Exito, $"DNI nuevo en {t.RazonSocialEmpresa}.")));
            filas.AddRange(ps.Documentos.Where(d => !d.YaExiste).Select(d => new FilaPlan($"{d.Dni} — {d.NombreTipoDocumento}", "Crear documento", TonoBadge.Exito, $"Emitido {d.FechaEmision:dd/MM/yyyy}.")));
            filas.AddRange(ps.Asignaciones.Where(a => !a.YaExiste).Select(a => new FilaPlan($"{a.Dni} → {a.NombreCentro}", "Crear asignación", TonoBadge.Exito, "Asignación nueva.")));
        }
        else if (_planCombinada is { } pc)
        {
            filas.AddRange(pc.Clientes.Where(c => !c.YaExiste).Select(c => new FilaPlan(c.RazonSocial, "Crear cliente", TonoBadge.Exito, $"CIF {c.Cif} nuevo.")));
            filas.AddRange(pc.Empresas.Where(e => !e.YaExiste).Select(e => new FilaPlan(e.RazonSocial, "Crear empresa", TonoBadge.Exito, "Razón social nueva.")));
            filas.AddRange(pc.Centros.Where(c => !c.YaExiste).Select(c => new FilaPlan(c.Nombre, "Crear centro", TonoBadge.Exito, $"Cliente {c.RazonSocialCliente}.")));
            filas.AddRange(pc.Trabajadores.Where(t => !t.YaExiste).Select(t => new FilaPlan($"{t.Nombre} {t.Apellidos} ({t.Dni})", "Crear trabajador", TonoBadge.Exito, $"DNI nuevo en {t.RazonSocialEmpresa}.")));
        }

        var advertencias = _planSimple?.Advertencias ?? _planCombinada?.Advertencias ?? [];
        var omitidos = _planSimple?.Omitidos ?? _planCombinada?.Omitidos ?? [];

        filas.AddRange(advertencias.Select(a => new FilaPlan(a.Descripcion, "Aviso", TonoBadge.Advertencia, a.Motivo)));
        filas.AddRange(omitidos.Select(o => new FilaPlan(o.Descripcion, "Omitir", TonoBadge.Peligro, o.Motivo)));

        return filas;
    }

    private void ContinuarAConfirmar()
    {
        _step = 4;
        _pasoMaximoAlcanzado = Math.Max(_pasoMaximoAlcanzado, 4);
    }

    private async Task ConfirmarImportacionAsync()
    {
        if (!_confirmado || !TienePlan) return;

        _importando = true;
        _mensajeError = null;
        StateHasChanged();

        try
        {
            if (_planSimple is not null)
            {
                var resultado = await Mediator.Send(new EjecutarImportacionCommand(_planSimple));
                if (resultado.EsFallido)
                {
                    await RegistrarFalloAsync(resultado.Error.Mensaje);
                    _mensajeError = resultado.Error.Mensaje;
                    return;
                }

                _resultadoSimple = resultado.Valor;
                await RegistrarExitoAsync(
                    _resultadoSimple.ClientesCreados + _resultadoSimple.CentrosCreados + _resultadoSimple.EmpresasCreadas
                        + _resultadoSimple.TrabajadoresCreados + _resultadoSimple.DocumentosCreados + _resultadoSimple.AsignacionesCreadas,
                    _resultadoSimple.Advertencias.Count, _resultadoSimple.Omitidos.Count);
            }
            else if (_planCombinada is not null)
            {
                var resultado = await Mediator.Send(new EjecutarImportacionCombinadaCommand(_planCombinada, _reemplazarExistentes));
                if (resultado.EsFallido)
                {
                    await RegistrarFalloAsync(resultado.Error.Mensaje);
                    _mensajeError = resultado.Error.Mensaje;
                    return;
                }

                _resultadoCombinada = resultado.Valor;
                await RegistrarExitoAsync(
                    _resultadoCombinada.ClientesCreados + _resultadoCombinada.EmpresasCreadas
                        + _resultadoCombinada.CentrosCreados + _resultadoCombinada.TrabajadoresCreados,
                    _resultadoCombinada.Advertencias.Count, _resultadoCombinada.Omitidos.Count);
            }

            ToastService.Mostrar("Importación completada.", TonoToast.Exito);
            _step = 5;
            _pasoMaximoAlcanzado = Math.Max(_pasoMaximoAlcanzado, 5);
            await CargarHistorialAsync();
        }
        catch (Exception)
        {
            await RegistrarFalloAsync("Excepción no controlada durante la importación.");
            _mensajeError = "No pudimos completar la importación. Intenta nuevamente en unos segundos.";
        }
        finally
        {
            _importando = false;
        }
    }

    private Task RegistrarExitoAsync(int totalCreados, int totalAdvertencias, int totalOmitidos) =>
        Mediator.Send(new RegistrarHistorialImportacionCommand(
            PlantillaActual.Titulo, _nombreArchivo ?? "(desconocido)", true, totalCreados, totalAdvertencias, totalOmitidos, null));

    private Task RegistrarFalloAsync(string mensaje) =>
        Mediator.Send(new RegistrarHistorialImportacionCommand(
            PlantillaActual.Titulo, _nombreArchivo ?? "(desconocido)", false, 0, 0, 0, mensaje));

    private void EmpezarDeNuevo()
    {
        _step = 1;
        _pasoMaximoAlcanzado = 1;
        _nombreArchivo = null;
        _mensajeError = null;
        _planSimple = null;
        _planCombinada = null;
        _confirmado = false;
        _reemplazarExistentes = false;
        _resultadoSimple = null;
        _resultadoCombinada = null;
    }
}
