using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Application.Importacion.Maestra;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Importacion;
using CaeManager.Domain.RelacionesEmpresariales;
using CaeManager.Domain.Trabajadores;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Importacion.Commands.EjecutarImportacionMaestra;

/// <summary>
/// Escribe un plan ya analizado por <see cref="Queries.AnalizarPlantillaMaestraQuery"/> (Paso 1 del Importador).
///
/// <see cref="ReemplazarExistentes"/> elige entre dos modos sobre un registro que ya existe (clave natural: CIF,
/// identidad, Cliente empresarial + nombre de Centro): <b>Completar vacíos</b> (<c>false</c>, por defecto) solo rellena
/// lo que el registro tiene vacío y nunca sobrescribe un dato presente; <b>Reemplazar</b> (<c>true</c>) deja ganar al
/// archivo cuando trae valor. En Reemplazar, cada valor no vacío que se sobrescribe se devuelve en
/// <see cref="ResultadoImportacionMaestraDto.ValoresSobrescritos"/> (hoja, fila, campo y valor previo): decisión N14,
/// sin reversión en v1. Una celda vacía del archivo nunca borra un dato.
///
/// Idempotencia: misma que la plantilla combinada — <c>Plan.OperacionId</c> y el índice único de
/// <see cref="OperacionImportacion"/> en el MISMO <c>SaveChangesAsync</c> que las altas.
/// Autorización: <see cref="IAutorizacionEscrituraEfectiva.EsActoDeAdministradorAsync"/>, como el resto de importaciones.
/// A diferencia de la plantilla combinada, NO es <see cref="IComandoDeAprovisionamiento"/>: ampliar esa lista explícita
/// es una decisión de autorización que este incremento no toma.
/// </summary>
public record EjecutarImportacionMaestraCommand(PlanImportacionMaestraDto Plan, bool ReemplazarExistentes)
    : ICommand<ResultadoImportacionMaestraDto>;

/// <summary>
/// Vista previa SIN escribir: devuelve exactamente lo que confirmar haría con este plan y este modo (cuántos campos se
/// rellenarían y qué valores previos se sobrescribirían), para el aviso de N14. Los valores previos pueden ser datos
/// personales: pasa por la misma autorización que el comando.
/// </summary>
public record PrevisualizarImportacionMaestraQuery(PlanImportacionMaestraDto Plan, bool ReemplazarExistentes)
    : IRequest<Result<ResultadoImportacionMaestraDto>>;

/// <param name="CamposRellenados">Campos que estaban vacíos y se rellenan (en cualquiera de los dos modos).</param>
/// <param name="ValoresSobrescritos">Solo en Reemplazar: un elemento por cada valor no vacío que el archivo sustituye.</param>
public record ResultadoImportacionMaestraDto(
    int EmpresasCreadas,
    int EmpresasActualizadas,
    int ClientesCreados,
    int ClientesActualizados,
    int RelacionesCreadas,
    int CentrosCreados,
    int CentrosActualizados,
    int TrabajadoresCreados,
    int TrabajadoresActualizados,
    int CamposRellenados,
    IReadOnlyList<ValorPrevioDto> ValoresSobrescritos,
    IReadOnlyList<ItemImportacionDto> Advertencias,
    IReadOnlyList<ItemImportacionDto> Omitidos)
{
    /// <summary>Confirmación repetida de una operación ya confirmada: no crea ni omite nada.</summary>
    public static ResultadoImportacionMaestraDto YaEjecutada() => new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, [], [],
        [new ItemImportacionDto("Importación", 0, "Archivo completo",
            "Esta importación ya se había confirmado antes (reintento o doble confirmación); no se ha vuelto a crear nada.")]);
}

public record ValorPrevioDto(string Hoja, int Fila, string Campo, string ValorPrevio, string? ValorNuevo);

public class EjecutarImportacionMaestraCommandHandler(
    IEmpresaRepository empresaRepositorio,
    IRelacionEmpresarialRepository relacionEmpresarialRepositorio,
    ICentroRepository centroRepositorio,
    ITrabajadorRepository trabajadorRepositorio,
    ICentrosQueryContext centrosContext, IEmpresasQueryContext empresasContext, ITrabajadoresQueryContext trabajadoresContext,
    IOperacionImportacionRepository operacionImportacionRepositorio,
    IAutorizacionEscrituraEfectiva autorizacionEscrituraEfectiva)
    : IRequestHandler<EjecutarImportacionMaestraCommand, Result<ResultadoImportacionMaestraDto>>,
      IRequestHandler<PrevisualizarImportacionMaestraQuery, Result<ResultadoImportacionMaestraDto>>
{
    private const string HojaEmpresas = "Empresas";
    private const string HojaClientes = "Clientes empresariales";
    private const string HojaCentros = "Centros";
    private const string HojaTrabajadores = "Trabajadores";

    public Task<Result<ResultadoImportacionMaestraDto>> Handle(
        EjecutarImportacionMaestraCommand request, CancellationToken cancellationToken) =>
        EjecutarAsync(request.Plan, request.ReemplazarExistentes, escribir: true, cancellationToken);

    public Task<Result<ResultadoImportacionMaestraDto>> Handle(
        PrevisualizarImportacionMaestraQuery request, CancellationToken cancellationToken) =>
        EjecutarAsync(request.Plan, request.ReemplazarExistentes, escribir: false, cancellationToken);

    private async Task<Result<ResultadoImportacionMaestraDto>> EjecutarAsync(
        PlanImportacionMaestraDto plan, bool reemplazar, bool escribir, CancellationToken cancellationToken)
    {
        if (!await autorizacionEscrituraEfectiva.EsActoDeAdministradorAsync(cancellationToken))
            return Result.Fallo<ResultadoImportacionMaestraDto>(Error.Crear(
                "Importacion.SoloAdministrador", "Solo Administrador puede ejecutar la importación de la plantilla maestra."));

        if (escribir && await operacionImportacionRepositorio.ExisteAsync(plan.OperacionId, cancellationToken))
            return ResultadoImportacionMaestraDto.YaEjecutada();

        // El índice único de CIF incluye las Empresas dadas de baja del Tenant propietario: una fila que coincida con
        // una baja abortaría el guardado con un error genérico.
        foreach (var cif in plan.Empresas.Select(e => e.Cif).Concat(plan.Clientes.Select(c => c.Cif)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var duplicada = await empresaRepositorio.ObtenerDuplicadaPorCifAsync(cif, cancellationToken: cancellationToken);
            if (duplicada?.EstaEliminado == true)
                return Result.Fallo<ResultadoImportacionMaestraDto>(DuplicadosEmpresa.ErrorDeBajaEnImportacion(cif));
        }

        try
        {
            return await ProcesarAsync(plan, reemplazar, escribir, cancellationToken);
        }
        catch
        {
            // Blazor Server comparte un único DbContext por circuito: una excepción a mitad deja entidades en Added.
            if (escribir) operacionImportacionRepositorio.DescartarPendientes();
            throw;
        }
    }

    private async Task<Result<ResultadoImportacionMaestraDto>> ProcesarAsync(
        PlanImportacionMaestraDto plan, bool reemplazar, bool escribir, CancellationToken cancellationToken)
    {
        if (escribir) operacionImportacionRepositorio.Agregar(OperacionImportacion.Registrar(plan.OperacionId));

        var omitidos = new List<ItemImportacionDto>();
        var sobrescritos = new List<ValorPrevioDto>();
        var rellenados = 0;
        var ahora = DateTime.UtcNow;

        // Valor resultante de un campo de texto bajo el modo elegido, contando rellenos y sobrescrituras.
        string? Texto(string hoja, int fila, string campo, string? actual, string? archivo)
        {
            if (string.IsNullOrWhiteSpace(archivo)) return actual;
            if (string.IsNullOrWhiteSpace(actual)) { rellenados++; return archivo; }
            if (!reemplazar || actual == archivo) return actual;
            sobrescritos.Add(new ValorPrevioDto(hoja, fila, campo, actual, archivo));
            return archivo;
        }

        DateOnly? Fecha(string hoja, int fila, string campo, DateOnly? actual, DateOnly? archivo)
        {
            if (archivo is null) return actual;
            if (actual is null) { rellenados++; return archivo; }
            if (!reemplazar || actual == archivo) return actual;
            sobrescritos.Add(new ValorPrevioDto(hoja, fila, campo, actual.Value.ToString("yyyy-MM-dd"), archivo.Value.ToString("yyyy-MM-dd")));
            return archivo;
        }

        // ---- Empresas gestionadas (CIF) ----
        var todas = await empresasContext.Empresas.ToListAsync(cancellationToken);
        var gestionadasPorCif = todas.Where(e => e.EsPropia && e.Cif != null).ToDictionary(e => e.Cif!, StringComparer.OrdinalIgnoreCase);
        var clientesPorCif = todas.Where(e => e.EsCritico != null && e.Cif != null).ToDictionary(e => e.Cif!, StringComparer.OrdinalIgnoreCase);
        var razonesEscritas = new HashSet<string>(StringComparer.Ordinal);

        async Task<Error?> RazonSocialOcupadaAsync(string razonSocial, Guid? excluirId) =>
            razonesEscritas.Contains(razonSocial.Trim())
                ? DuplicadosEmpresa.ErrorRazonSocialDuplicada(dadaDeBaja: false)
                : await DuplicadosEmpresa.ComprobarRazonSocialAsync(empresaRepositorio, razonSocial, excluirId, cancellationToken: cancellationToken);

        var empresasCreadas = 0;
        var empresasActualizadas = 0;
        foreach (var fila in plan.Empresas)
        {
            if (gestionadasPorCif.TryGetValue(fila.Cif, out var existente))
            {
                var rellenadosAntes = rellenados;
                var razonSocial = Texto(HojaEmpresas, fila.Fila, "Razón social", existente.RazonSocial, fila.RazonSocial)!;
                if (razonSocial != existente.RazonSocial && await RazonSocialOcupadaAsync(razonSocial, existente.Id) is { } ocupada)
                {
                    omitidos.Add(new(HojaEmpresas, fila.Fila, fila.RazonSocial, ocupada.Mensaje));
                    sobrescritos.RemoveAll(v => v.Hoja == HojaEmpresas && v.Fila == fila.Fila);
                    rellenados = rellenadosAntes;
                    continue;
                }
                var cnae = Texto(HojaEmpresas, fila.Fila, "CNAE", existente.Cnae, fila.Cnae);
                var convenio = Texto(HojaEmpresas, fila.Fila, "Convenio aplicable", existente.ConvenioAplicable, fila.ConvenioAplicable);
                var anexo = existente.EsActividadAnexoI;
                if (reemplazar && fila.EsActividadAnexoI is { } anexoArchivo && anexoArchivo != anexo)
                {
                    sobrescritos.Add(new(HojaEmpresas, fila.Fila, "Actividad Anexo I", anexo ? "S" : "N", anexoArchivo ? "S" : "N"));
                    anexo = anexoArchivo;
                }

                if (razonSocial != existente.RazonSocial || cnae != existente.Cnae || convenio != existente.ConvenioAplicable
                    || anexo != existente.EsActividadAnexoI)
                {
                    try
                    {
                        if (escribir) existente.Actualizar(razonSocial, existente.Cif, cnae, convenio, anexo);
                        razonesEscritas.Add(razonSocial.Trim());
                        empresasActualizadas++;
                    }
                    catch (ArgumentException ex)
                    {
                        omitidos.Add(new(HojaEmpresas, fila.Fila, fila.RazonSocial, ex.Message));
                    }
                }
                continue;
            }

            if (await RazonSocialOcupadaAsync(fila.RazonSocial, null) is { } razonOcupada)
            {
                omitidos.Add(new(HojaEmpresas, fila.Fila, fila.RazonSocial, razonOcupada.Mensaje));
                continue;
            }

            try
            {
                var empresa = new Empresa(fila.RazonSocial, fila.Cif, fila.Cnae, fila.ConvenioAplicable, fila.EsActividadAnexoI ?? false);
                if (escribir) empresaRepositorio.Agregar(empresa);
                gestionadasPorCif[fila.Cif] = empresa;
                razonesEscritas.Add(empresa.RazonSocial);
                empresasCreadas++;
            }
            catch (ArgumentException ex)
            {
                omitidos.Add(new(HojaEmpresas, fila.Fila, fila.RazonSocial, ex.Message));
            }
        }

        // ---- Clientes empresariales (CIF) y su relación con la Empresa gestionada ----
        var clientesCreados = 0;
        var clientesActualizados = 0;
        var relacionesCreadas = 0;
        var clientesTratados = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paresRelacionados = new HashSet<(Guid, Guid)>();
        var relacionesVigentes = (await (
            from r in empresasContext.RelacionesEmpresariales
            where r.VigenciaHasta == null
            select new { r.ProveedoraId, r.ClienteId }).ToListAsync(cancellationToken))
            .Select(r => (r.ProveedoraId, r.ClienteId)).ToHashSet();

        foreach (var fila in plan.Clientes)
        {
            if (!gestionadasPorCif.TryGetValue(fila.CifEmpresaGestionada, out var gestionada))
            {
                omitidos.Add(new(HojaClientes, fila.Fila, fila.RazonSocial,
                    $"No se pudo crear o encontrar la Empresa gestionada con CIF \"{fila.CifEmpresaGestionada}\"."));
                continue;
            }

            Empresa cliente;
            if (clientesPorCif.TryGetValue(fila.Cif, out var existente))
            {
                cliente = existente;
                // Una Empresa que ya existía puede venir repetida por otra pareja: solo la primera fila la toca.
                if (clientesTratados.Add(fila.Cif))
                {
                    var rellenadosAntes = rellenados;
                    var razonSocial = Texto(HojaClientes, fila.Fila, "Razón social", existente.RazonSocial, fila.RazonSocial)!;
                    var notas = Texto(HojaClientes, fila.Fila, "Nota interna", existente.Notas, fila.Nota);
                    var critico = existente.EsCritico ?? false;
                    if (reemplazar && critico != fila.EsCritico)
                    {
                        sobrescritos.Add(new(HojaClientes, fila.Fila, "Crítico", critico ? "C" : "N", fila.EsCritico ? "C" : "N"));
                        critico = fila.EsCritico;
                    }

                    if (razonSocial != existente.RazonSocial && await RazonSocialOcupadaAsync(razonSocial, existente.Id) is { } ocupada)
                    {
                        omitidos.Add(new(HojaClientes, fila.Fila, fila.RazonSocial, ocupada.Mensaje));
                        sobrescritos.RemoveAll(v => v.Hoja == HojaClientes && v.Fila == fila.Fila);
                        rellenados = rellenadosAntes;
                        continue;
                    }

                    if (razonSocial != existente.RazonSocial || notas != existente.Notas || critico != existente.EsCritico)
                    {
                        if (escribir) existente.ActualizarComoCliente(razonSocial, existente.Cif!, critico, notas);
                        razonesEscritas.Add(razonSocial.Trim());
                        clientesActualizados++;
                    }
                }
            }
            else
            {
                if (await RazonSocialOcupadaAsync(fila.RazonSocial, null) is { } ocupada)
                {
                    omitidos.Add(new(HojaClientes, fila.Fila, fila.RazonSocial, ocupada.Mensaje));
                    continue;
                }
                try
                {
                    cliente = Empresa.CrearComoCliente(fila.RazonSocial, fila.Cif, fila.EsCritico, fila.Nota, ejecutivoUsuarioId: null);
                }
                catch (ArgumentException ex)
                {
                    omitidos.Add(new(HojaClientes, fila.Fila, fila.RazonSocial, ex.Message));
                    continue;
                }
                if (escribir) empresaRepositorio.Agregar(cliente);
                clientesPorCif[fila.Cif] = cliente;
                clientesTratados.Add(fila.Cif);
                razonesEscritas.Add(cliente.RazonSocial);
                clientesCreados++;
            }

            // Relación Empresa gestionada → Cliente empresarial, una por pareja y solo si no estaba vigente.
            var par = (gestionada.Id, cliente.Id);
            if (!relacionesVigentes.Contains(par) && paresRelacionados.Add(par))
            {
                if (escribir)
                    await relacionEmpresarialRepositorio.AgregarSiNoVigenteAsync(gestionada.Id, cliente.Id, ahora, cancellationToken: cancellationToken);
                relacionesCreadas++;
            }
        }

        // ---- Centros (Cliente empresarial + nombre) ----
        var clientePorId = todas.Concat(clientesPorCif.Values).Concat(gestionadasPorCif.Values).DistinctBy(e => e.Id).ToDictionary(e => e.Id);
        static string ClaveCentro(Guid idDelCliente, string nombre) => $"{idDelCliente:N}|{nombre}";
        var centrosPorClave = CatalogoPorNombre<Centro>.De(
            await centrosContext.Centros.ToListAsync(cancellationToken), c => ClaveCentro(c.ClienteId, c.Nombre), c => c);
        var centrosCreados = 0;
        var centrosActualizados = 0;

        foreach (var fila in plan.Centros)
        {
            if (!clientesPorCif.TryGetValue(fila.CifCliente, out var cliente) || !gestionadasPorCif.TryGetValue(fila.CifEmpresaGestionada, out var gestionada))
            {
                omitidos.Add(new(HojaCentros, fila.Fila, fila.Nombre, "No se pudo crear o encontrar el Cliente empresarial o la Empresa gestionada de este Centro."));
                continue;
            }

            var busqueda = centrosPorClave.Buscar(ClaveCentro(cliente.Id, fila.Nombre), out var existente);
            if (busqueda == ResultadoBusquedaPorNombre.Ambiguo)
            {
                omitidos.Add(new(HojaCentros, fila.Fila, fila.Nombre,
                    $"Hay varios Centros llamados \"{fila.Nombre}\" en este Cliente empresarial (el nombre solo cambia en mayúsculas); la importación no puede saber cuál actualizar. Unifica o renombra los duplicados y vuelve a importar."));
                continue;
            }

            if (busqueda == ResultadoBusquedaPorNombre.Unico && existente is not null)
            {
                var codigo = Texto(HojaCentros, fila.Fila, "Código del Centro", existente.CodigoCentro, fila.CodigoCentro);
                var direccion = Texto(HojaCentros, fila.Fila, "Dirección", existente.Direccion, fila.Direccion);
                var contacto = Texto(HojaCentros, fila.Fila, "Contacto", existente.Contacto, fila.Contacto);
                var contrato = Fecha(HojaCentros, fila.Fila, "Contrato vigente hasta", existente.ContratoVigenteHasta, fila.ContratoVigenteHasta);
                var modalidad = fila.SinGestionCae ? ModalidadGestionCae.SinGestionCae : ModalidadGestionCae.ConGestionCae;
                var modalidadResultante = existente.GestionCae;
                if (reemplazar && modalidad != existente.GestionCae)
                {
                    sobrescritos.Add(new(HojaCentros, fila.Fila, "Modalidad de gestión CAE",
                        existente.GestionCae == ModalidadGestionCae.SinGestionCae ? "Sin gestión CAE" : "Con gestión CAE",
                        fila.SinGestionCae ? "Sin gestión CAE" : "Con gestión CAE"));
                    modalidadResultante = modalidad;
                }

                if (codigo != existente.CodigoCentro || direccion != existente.Direccion || contacto != existente.Contacto
                    || contrato != existente.ContratoVigenteHasta || modalidadResultante != existente.GestionCae)
                {
                    if (escribir)
                    {
                        existente.Actualizar(existente.Nombre, codigo, direccion, contacto, contrato);
                        existente.EstablecerGestionCae(modalidadResultante);
                    }
                    centrosActualizados++;
                }
                continue;
            }

            try
            {
                var centro = new Centro(cliente.Id, gestionada.Id, fila.Nombre, fila.CodigoCentro, fila.Direccion, fila.Contacto, fila.ContratoVigenteHasta);
                if (fila.SinGestionCae) centro.EstablecerGestionCae(ModalidadGestionCae.SinGestionCae);
                if (escribir) centroRepositorio.Agregar(centro);
                centrosPorClave.Agregar(ClaveCentro(cliente.Id, fila.Nombre), centro);
                centrosCreados++;
            }
            catch (ArgumentException ex)
            {
                omitidos.Add(new(HojaCentros, fila.Fila, fila.Nombre, ex.Message));
            }
        }

        // ---- Trabajadores (identidad) ----
        var trabajadoresPorIdentidad = await trabajadoresContext.Trabajadores
            .Where(t => t.Dni != null)
            .ToDictionaryAsync(t => t.Dni!, StringComparer.OrdinalIgnoreCase, cancellationToken);
        var enPapelera = await DnisEnPapelera.ObtenerAsync(
            trabajadorRepositorio, plan.Trabajadores.Select(t => t.Identidad).Where(i => !trabajadoresPorIdentidad.ContainsKey(i)), cancellationToken);
        var trabajadoresCreados = 0;
        var trabajadoresActualizados = 0;

        foreach (var fila in plan.Trabajadores)
        {
            var descripcion = $"{fila.Nombre} {fila.Apellidos}";
            // Una identidad en la Papelera no está entre los activos, pero el índice (TenantId, Dni) la incluye.
            if (!trabajadoresPorIdentidad.ContainsKey(fila.Identidad) && enPapelera.Contains(DnisEnPapelera.Normalizar(fila.Identidad)))
            {
                omitidos.Add(new(HojaTrabajadores, fila.Fila, descripcion, DnisEnPapelera.Motivo(fila.Identidad)));
                continue;
            }

            if (!gestionadasPorCif.TryGetValue(fila.CifEmpresa, out var empresa))
            {
                omitidos.Add(new(HojaTrabajadores, fila.Fila, descripcion, $"No se pudo crear o encontrar la Empresa gestionada con CIF \"{fila.CifEmpresa}\"."));
                continue;
            }

            if (trabajadoresPorIdentidad.TryGetValue(fila.Identidad, out var existente))
            {
                var nombre = Texto(HojaTrabajadores, fila.Fila, "Nombre", existente.Nombre, fila.Nombre)!;
                var apellidos = Texto(HojaTrabajadores, fila.Fila, "Apellidos", existente.Apellidos, fila.Apellidos)!;
                var nacimiento = Fecha(HojaTrabajadores, fila.Fila, "Fecha de nacimiento", existente.FechaNacimiento, fila.FechaNacimiento);
                var email = Texto(HojaTrabajadores, fila.Fila, "Correo", existente.Email, fila.Email);
                var telefono = Texto(HojaTrabajadores, fila.Fila, "Teléfono", existente.Telefono, fila.Telefono);
                var puesto = Texto(HojaTrabajadores, fila.Fila, "Categoría / Puesto", existente.Puesto, fila.Puesto);
                var observaciones = Texto(HojaTrabajadores, fila.Fila, "Observaciones", existente.Observaciones, fila.Observaciones);

                if (nombre != existente.Nombre || apellidos != existente.Apellidos || nacimiento != existente.FechaNacimiento
                    || email != existente.Email || telefono != existente.Telefono || puesto != existente.Puesto
                    || observaciones != existente.Observaciones)
                {
                    try
                    {
                        if (escribir) existente.Actualizar(nombre, apellidos, nacimiento, email, observaciones, existente.Alias, telefono, puesto);
                        trabajadoresActualizados++;
                    }
                    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                    {
                        omitidos.Add(new(HojaTrabajadores, fila.Fila, descripcion, ex.Message));
                    }
                }
                continue;
            }

            try
            {
                var trabajador = Trabajador.DeEmpresa(empresa.Id, fila.Nombre, fila.Apellidos, fila.Identidad, fila.FechaNacimiento,
                    fila.Email, fila.Observaciones, alias: null, fila.Telefono, fila.Puesto);
                if (escribir) trabajadorRepositorio.Agregar(trabajador);
                trabajadoresPorIdentidad[fila.Identidad] = trabajador;
                trabajadoresCreados++;
            }
            catch (ArgumentException ex)
            {
                omitidos.Add(new(HojaTrabajadores, fila.Fila, descripcion, ex.Message));
            }
        }

        // Solo se cuentan las sobrescrituras de filas que SÍ se aplicaron (una fila omitida a mitad no escribe).
        var omitidasPorFila = omitidos.Select(o => (o.Hoja, o.Fila)).ToHashSet();
        sobrescritos.RemoveAll(v => omitidasPorFila.Contains((v.Hoja, v.Fila)));

        var resultado = new ResultadoImportacionMaestraDto(
            empresasCreadas, empresasActualizadas, clientesCreados, clientesActualizados, relacionesCreadas,
            centrosCreados, centrosActualizados, trabajadoresCreados, trabajadoresActualizados, rellenados,
            sobrescritos, plan.Advertencias, [.. plan.Omitidos, .. omitidos]);

        if (!escribir) return resultado;

        // Todo lo pendiente junto con la marca de la operación en un único guardado: si dos confirmaciones de la
        // MISMA operación compiten, la perdedora choca en el índice único y no escribe nada.
        return await operacionImportacionRepositorio.GuardarSiOperacionNuevaAsync(cancellationToken)
            ? resultado
            : ResultadoImportacionMaestraDto.YaEjecutada();
    }
}
