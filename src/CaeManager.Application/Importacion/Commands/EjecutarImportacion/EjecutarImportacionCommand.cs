using CaeManager.Application.Asignaciones;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Documentos.Acreditacion;
using CaeManager.Application.Empresas;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Application.Importacion;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Importacion;
using CaeManager.Domain.Trabajadores;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Importacion.Commands.EjecutarImportacion;

/// <summary>
/// Escribe en la base de datos un plan ya analizado por
/// AnalizarImportacionExcelQuery — el plan viaja completo en el propio
/// comando porque Blazor Server ya lo tiene en memoria en el circuito del
/// usuario (se generó al analizar, un paso antes) y releer el Excel dos
/// veces sería trabajo duplicado. Reutiliza entidades ya existentes por
/// nombre/DNI en vez de duplicarlas — importar el mismo archivo dos veces
/// no crea filas repetidas.
///
/// Idempotencia bajo concurrencia (REC-108, DEC-20): <c>Plan.OperacionId</c>
/// es la identidad estable de ESTA operación, generada una sola vez al
/// analizar (ver PlanImportacionDto) y transportada sin tocar hasta aquí —
/// el flag <c>_confirmado</c>/<c>_importando</c> del circuito Blazor
/// (Importacion.razor.cs) es cortesía de UI, no defensa. La defensa real es
/// <see cref="OperacionImportacion"/>: su índice único de
/// <c>(TenantId, OperacionId)</c> hace que, si dos confirmaciones de la
/// MISMA operación compiten, la perdedora choque en ese índice dentro del
/// mismo <c>SaveChangesAsync</c> que sus propias inserciones — su
/// transacción entera se descarta, así que ninguna de sus filas llega a
/// escribirse. Por diseño (DEC-20) esto NO usa <c>(Trabajador,
/// TipoDocumento)</c> como clave — esa cardinalidad de Documento sigue sin
/// tocarse — y no persiste una clave por entrada del plan: al fallar la
/// operación completa antes de comprometer nada, "cada entrada como máximo
/// una vez" se cumple sin necesidad de perseguir cada fila por separado.
/// </summary>
/// <summary>
/// <see cref="IComandoDeAprovisionamiento" /> (PD-A3): forma parte del alta
/// planificada de contenido CAE en un tenant durante su aprovisionamiento
/// inicial — ver <c>AutorizacionEscrituraBehavior</c>.
/// </summary>
public record EjecutarImportacionCommand(PlanImportacionDto Plan)
    : ICommand<ResultadoImportacionDto>, IComandoDeAprovisionamiento, Common.ILecturaRegistradaEnSesionPrivilegiada;

public record ResultadoImportacionDto(
    int ClientesCreados,
    int CentrosCreados,
    int EmpresasCreadas,
    int TrabajadoresCreados,
    int DocumentosCreados,
    int AsignacionesCreadas,
    IReadOnlyList<ItemImportacionDto> Advertencias,
    IReadOnlyList<ItemImportacionDto> Omitidos)
{
    /// <summary>
    /// Resultado de una confirmación que perdió la carrera de idempotencia
    /// (REC-108): la operación ya se había confirmado, antes o en paralelo, así
    /// que esta ejecución no creó ni omitió nada — no es un fallo que reportar
    /// al usuario, es el mismo archivo ya importado.
    /// </summary>
    public static ResultadoImportacionDto YaEjecutada() => new(0, 0, 0, 0, 0, 0, [],
        [new ItemImportacionDto("Importación", 0, "Archivo completo",
            "Esta importación ya se había confirmado antes (reintento o doble confirmación); no se ha vuelto a crear nada.")]);
}

/// <summary>
/// Invariante «nada se descarta en silencio» (Project-Hydra-Negocio/tecnico/IMPORTACION.md § 3 bis,
/// ratificada por DCR-12 decisión B, propietario 2026-08-24): la importación
/// admite éxito parcial con errores reportados, pero ninguna fila de un flujo
/// soportado —Centros_Plataformas incluido— puede desaparecer sin quedar
/// registrada en <see cref="ResultadoImportacionDto.Omitidos"/> con hoja,
/// fila, descripción y motivo.
///
/// La misma decisión instruye explícitamente NO uniformizar las ramas de
/// descarte: el contrato es uniforme, la implementación no. Cada rama nombra
/// su causal concreta —qué dependencia faltó y por qué— en vez de un motivo
/// genérico, y la de Asignación→Centro distingue el Centro que el archivo ni
/// siquiera declaraba del que venía en Centros_Plataformas pero no pudo
/// crearse en esta misma importación.
///
/// Dos causales más, con su propio motivo y sin abortar el archivo (C-06, C-07):
/// un nombre AMBIGUO —dos Centros homónimos bajo distintos Clientes empresariales,
/// o dos razones sociales o Tipos de documento que solo difieren en mayúsculas—
/// omite solo las filas que lo citan (Asignaciones, Trabajadores, Documentos), nunca
/// se resuelve al primero que aparezca; y un DNI de un Trabajador que está en la
/// Papelera (el índice único incluye a los eliminados) omite el alta, el Documento
/// o la Asignación de ese DNI con «restáuralo», sin revelar datos del eliminado, en
/// vez de dejar que el guardado entero falle con un 23505.
///
/// Excepción por ámbito (cargas masivas acotadas, decisión de Chris del 2026-11-01): con autoridad acotada y una lista
/// de cartera que recorta, la causal concreta de una dependencia que la fila NO intenta crear («no existe», «está en la
/// Papelera») se sustituye por el mismo motivo uniforme que «fuera de tu ámbito» (<see cref="AmbitoCargaMasiva.MotivoSinDistinguirExistencia"/>):
/// nombrarla revelaría a quien no ve lo existente qué existe en el Tenant. La fila sigue registrada con hoja, fila y
/// descripción; con autoridad Total, o sin lista que recorte esa entidad, las causales accionables no cambian.
///
/// La deduplicación es la excepción razonada, no un olvido: reutilizar una
/// entidad que ya existe no es descartar una fila. El paso de análisis ya lo
/// anuncia marcando la fila <c>YaExiste</c>, y la vista previa no la cuenta
/// como «Crear …» (Importacion.razor.cs cuenta solo <c>!YaExiste</c>). Solo
/// se registra el caso en que el plan SÍ prometió crearla y el estado cambió
/// entre analizar y confirmar — si no, la pantalla final prometería «1 nueva»
/// y confirmaría «0» sin explicación.
/// </summary>
public class EjecutarImportacionCommandHandler(
    IEmpresaRepository empresaRepositorio,
    ITrabajadorRepository trabajadorRepositorio,
    IDocumentoRepository documentoRepositorio,
    IAsignacionRepository asignacionRepositorio,
    IOperacionImportacionRepository operacionImportacionRepositorio,
    IAsignacionesQueryContext asignacionesContext, ICentrosQueryContext centrosContext, IDocumentosQueryContext documentosContext, IEmpresasQueryContext empresasContext, ITiposDocumentoQueryContext tiposDocumentoContext, ITrabajadoresQueryContext trabajadoresContext,
    IAutorizacionEscrituraEfectiva autorizacionEscrituraEfectiva,
    IAltaAcreditacionesPlataformaService altaAcreditaciones,
    IAlcanceDatosService alcanceDatos,
    ISerializacionAltasAcreditacionEmpresa serializacionAltas,
    ILimiteEmpresaPropia limiteEmpresaPropia)
    : IRequestHandler<EjecutarImportacionCommand, Result<ResultadoImportacionDto>>
{
    // Restaura en Application el límite que ya exponen las 4 páginas de importación (@attribute [Authorize(Roles = ConCargaMasivaCsv)]):
    // sin este guard, cualquier llamador de MediatR que no sea esa página exacta podía importar como GestorCae, Consulta o Cliente.
    // Ejecutan el Administrador (rol, o Sesión Privilegiada de Aprovisionamiento sobre este Tenant, sin recorte de ámbito), la Dirección CAE y el
    // Coordinador CAE (cada fila contrastada con su ámbito efectivo, AmbitoCargaMasiva; lo que cae fuera se omite y se cuenta).
    // IAutorizacionEscrituraEfectiva, no el literal "Administrador": bajo una
    // sesión de Aprovisionamiento (PD-A3) el rol de negocio es null, así que
    // la comparación literal bloquearía también el alta de tenant que PD-A3
    // acaba de abrir. Ver IAutorizacionEscrituraEfectiva para las dos ramas.
    public async Task<Result<ResultadoImportacionDto>> Handle(EjecutarImportacionCommand request, CancellationToken cancellationToken)
    {
        var autoridad = await autorizacionEscrituraEfectiva.ObtenerAutoridadCargaMasivaAsync(cancellationToken);
        if (autoridad == AutoridadCargaMasiva.Ninguna)
            return Result.Fallo<ResultadoImportacionDto>(ErroresCargaMasiva.SinAutoridad);

        var plan = request.Plan;

        // Reintento secuencial (doble clic, reintento tras un timeout de red) de
        // la MISMA operación ya confirmada: se corta aquí, antes de reconstruir
        // el plan entero, con el mismo resultado limpio que la carrera real
        // produce más abajo. No es la defensa — es solo evitar trabajo; la
        // defensa es el índice único que protege el SaveChangesAsync final.
        if (await operacionImportacionRepositorio.ExisteAsync(plan.OperacionId, cancellationToken))
            return ResultadoImportacionDto.YaEjecutada();

        try
        {
            return await ConstruirYConfirmarAsync(plan, autoridad, cancellationToken);
        }
        catch
        {
            // Blazor Server comparte un único DbContext por circuito (el mismo que
            // usa RegistrarHistorialImportacionCommand, disparado justo después de
            // esta llamada — ver Importacion.razor.cs): sin este descarte, una
            // excepción a mitad de construir el plan dejaría entidades en estado
            // Added (incluida la propia marca de OperacionImportacion) que ese
            // guardado NO relacionado intentaría persistir también, filtrando
            // escritura parcial o repitiendo el mismo choque de unicidad sin que
            // nadie lo capture. Ver el comentario de IOperacionImportacionRepository.DescartarPendientes.
            operacionImportacionRepositorio.DescartarPendientes();
            throw;
        }
    }

    private async Task<Result<ResultadoImportacionDto>> ConstruirYConfirmarAsync(
        PlanImportacionDto plan, AutoridadCargaMasiva autoridad, CancellationToken cancellationToken)
    {
        var ambito = await AmbitoCargaMasiva.ResolverAsync(autoridad, alcanceDatos, cancellationToken);
        operacionImportacionRepositorio.Agregar(OperacionImportacion.Registrar(plan.OperacionId));

        var hoy = DiaDeNegocio.Hoy();

        // F3c (2026-08-28): leía la tabla legacy Clientes, congelada desde
        // F3b — un Cliente creado tras el freeze no se reconocía al importar y
        // su fila se omitía con el motivo "no existe todavía". "Cliente" es la
        // Empresa contraparte con EsCritico != null, mismo discriminador que
        // ObtenerClientesQuery.
        //
        // C-06: los emparejamientos por nombre (Cliente, Centro, razón social y Tipo de documento) usan
        // CatalogoPorNombre y no ToDictionaryAsync/TryAdd: dos Centros homónimos bajo distintos Clientes
        // empresariales, o dos razones sociales o Tipos de documento que solo difieren en mayúsculas, son datos
        // legítimos (los índices únicos distinguen mayúsculas) y un diccionario con clave repetida tumbaba la
        // importación entera. Un nombre ambiguo omite SOLO las filas que lo citan, con su causal propia.
        var clientesPorNombre = CatalogoPorNombre<Guid>.De(
            await empresasContext.Empresas.Where(e => e.EsCritico != null)
                .Select(e => new { e.RazonSocial, e.Id }).ToListAsync(cancellationToken),
            e => e.RazonSocial, e => e.Id);
        clientesPorNombre.EsVisible = ambito.Cliente;
        var centrosPorNombre = CatalogoPorNombre<Guid>.De(
            await centrosContext.Centros.Select(c => new { c.Nombre, c.Id }).ToListAsync(cancellationToken),
            c => c.Nombre, c => c.Id);
        centrosPorNombre.EsVisible = ambito.Centro;
        var empresasPorRazonSocial = CatalogoPorNombre<Guid>.De(
            await empresasContext.Empresas.Select(e => new { e.RazonSocial, e.Id }).ToListAsync(cancellationToken),
            e => e.RazonSocial, e => e.Id);
        empresasPorRazonSocial.EsVisible = ambito.Empresa;
        // Un trabajador anonimizado (Dni null) no puede ser destino de un
        // emparejamiento por DNI: queda fuera del diccionario.
        var trabajadoresPorDni = await trabajadoresContext.Trabajadores
            .Where(t => t.Dni != null)
            .ToDictionaryAsync(t => t.Dni!, t => t.Id, StringComparer.OrdinalIgnoreCase, cancellationToken);
        var tiposDocumentoPorNombre = CatalogoPorNombre<TipoDocumento>.De(
            await tiposDocumentoContext.TiposDocumento.ToListAsync(cancellationToken), t => t.Nombre, t => t);

        // C-07: el índice (TenantId, Dni) incluye a los Trabajadores de la Papelera, que el diccionario de arriba no
        // ve (filtro global). Un alta con uno de esos DNI haría fallar el guardado entero con un 23505 genérico; se
        // detectan una vez, de los DNI del plan que no están activos, y la fila se omite con su causal.
        var dnisEnPapelera = await DnisEnPapelera.ObtenerAsync(
            trabajadorRepositorio,
            plan.Trabajadores.Select(t => t.Dni).Concat(plan.Documentos.Select(d => d.Dni)).Concat(plan.Asignaciones.Select(a => a.Dni))
                .Where(dni => !trabajadoresPorDni.ContainsKey(dni)),
            cancellationToken);
        bool EstaEnPapelera(string dni) =>
            !trabajadoresPorDni.ContainsKey(dni) && dnisEnPapelera.Contains(DnisEnPapelera.Normalizar(dni));

        var clientesCreados = 0;
        var centrosCreados = 0;
        var empresasCreadas = 0;
        var limiteAvisado = false; // D-P11: un solo aviso a Soporte TALVEG por comando, no uno por fila
        var trabajadoresCreados = 0;
        var documentosCreados = 0;
        var asignacionesCreadas = 0;

        // Cada intento de construcción va protegido: una fila del Excel con un
        // dato inesperado (un nombre demasiado largo, un formato que el parser
        // no detectó) no debe abortar el resto de una importación por lo demás
        // válida — se reporta como omitida igual que los casos ya detectados
        // al analizar.
        var omitidosEnEscritura = new List<ItemImportacionDto>();
        var advertenciasEnEscritura = new List<ItemImportacionDto>();

        // Altas de esta importación que pueden hacer nacer acreditaciones de
        // plataforma (ver IAltaAcreditacionesPlataformaService más abajo).
        var trabajadoresNuevos = new List<Trabajador>();
        var documentosNuevos = new List<Documento>();
        var asignacionesNuevas = new List<Asignacion>();

        // Centros que este archivo declaraba en Centros_Plataformas, con lo que el
        // análisis sabía de cada uno. Sirve para que una Asignación huérfana
        // distinga sus tres causales: el centro que el archivo ni menciona, el que
        // venía en el archivo y no pudo crearse, y el que sí existía al analizar y
        // ha dejado de existir antes de confirmar.
        var centrosDeclaradosEnElArchivo = plan.ClientesCentros
            .GroupBy(c => c.Nombre, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().YaExisteCentro, StringComparer.OrdinalIgnoreCase);

        foreach (var fila in plan.ClientesCentros)
        {
            // Cliente ahora exige CIF y Centro exige Empresa (Fase 10) — ninguno de
            // los dos formatos de Excel soportados hoy recoge esos datos todavía,
            // así que aquí solo se reutilizan Cliente/Centro que ya existían antes
            // de importar. Crear cualquiera de los dos de cero se deja para cuando
            // la plantilla incluya esos campos; mientras tanto se omite con un
            // motivo explícito en vez de fallar o inventar un CIF/Empresa falsos.
            //
            // Esta hoja nunca intenta crear: con ámbito acotado, un Cliente empresarial o un Centro que no existe y uno que
            // existe fuera del ámbito se omiten con el mismo motivo uniforme (MotivoSinDistinguirExistencia). Decir «no existe»
            // solo a lo inexistente, y nada a lo existente fuera del ámbito, revelaría qué existe en el Tenant sin el
            // intento de alta que justifica el oráculo de clave única aceptado.
            const string QuePideClienteOCentro = "El Cliente empresarial o el centro de esta fila";
            var busquedaCliente = clientesPorNombre.Buscar(fila.Nombre, out var idDelClienteEmpresarial);
            if (busquedaCliente == ResultadoBusquedaPorNombre.Unico && !ambito.Cliente(idDelClienteEmpresarial))
            {
                omitidosEnEscritura.Add(new ItemImportacionDto(
                    fila.Hoja, fila.Fila, fila.Nombre, AmbitoCargaMasiva.Motivo(QuePideClienteOCentro)));
                continue;
            }

            if (busquedaCliente != ResultadoBusquedaPorNombre.Unico)
            {
                omitidosEnEscritura.Add(new ItemImportacionDto(
                    fila.Hoja, fila.Fila, fila.Nombre,
                    busquedaCliente == ResultadoBusquedaPorNombre.Ambiguo
                        ? $"Hay varios Clientes empresariales llamados «{fila.Nombre}» (el nombre solo cambia en mayúsculas); la importación no puede saber a cuál se refiere esta fila."
                        : ambito.MotivoSinDistinguirExistencia(QuePideClienteOCentro, EntidadDelAmbito.Cliente,
                            "Este Cliente no existe todavía. Ahora requiere un CIF, que esta plantilla no recoge — créalo manualmente en Clientes.")));
                continue;
            }

            // Un Centro homónimo (ambiguo) sí existe: esta fila no promete crear nada. La ambigüedad se reporta en
            // las Asignaciones que lo citan, que son las que no pueden elegir.
            var busquedaCentroDeLaFila = centrosPorNombre.Buscar(fila.Nombre, out var centroDeLaFilaId);
            if (busquedaCentroDeLaFila == ResultadoBusquedaPorNombre.NoExiste)
            {
                omitidosEnEscritura.Add(new ItemImportacionDto(
                    fila.Hoja, fila.Fila, fila.Nombre,
                    ambito.MotivoSinDistinguirExistencia(QuePideClienteOCentro, EntidadDelAmbito.Centro,
                        "Este centro no existe todavía. Ahora requiere una Empresa asociada, que esta plantilla no recoge — créalo manualmente en Centros.")));
            }
            else if (busquedaCentroDeLaFila == ResultadoBusquedaPorNombre.Unico && !ambito.Centro(centroDeLaFilaId))
            {
                omitidosEnEscritura.Add(new ItemImportacionDto(
                    fila.Hoja, fila.Fila, fila.Nombre, AmbitoCargaMasiva.Motivo(QuePideClienteOCentro)));
            }
        }

        foreach (var fila in plan.Empresas)
        {
            var busquedaEmpresa = empresasPorRazonSocial.Buscar(fila.RazonSocial);
            if (busquedaEmpresa == ResultadoBusquedaPorNombre.Ambiguo)
            {
                // No hay una Empresa que reutilizar ni se crea otra: la fila se omite con su causal, y los
                // Trabajadores que la citen la repiten (no se les asigna a una Empresa elegida al azar).
                omitidosEnEscritura.Add(new ItemImportacionDto(
                    fila.Hoja, fila.Fila, fila.RazonSocial, MotivoVariasEmpresas(fila.RazonSocial)));
                continue;
            }

            if (busquedaEmpresa == ResultadoBusquedaPorNombre.Unico)
            {
                empresasPorRazonSocial.Buscar(fila.RazonSocial, out var empresaExistenteId);
                // Reutilización, no descarte: el análisis ya la marcó YaExiste y la
                // vista previa no prometió crearla. Solo se registra si el plan sí
                // la prometía y apareció entre analizar y confirmar. Una Empresa fuera del ámbito no se reutiliza
                // ni se describe (decir «ya existía» a quien no la ve revelaría que existe): se omite y se cuenta.
                // Una segunda fila de una Empresa que esta carga acaba de crear no la oculta: la creó quien carga.
                if (!ambito.Empresa(empresaExistenteId))
                    omitidosEnEscritura.Add(new ItemImportacionDto(
                        fila.Hoja, fila.Fila, fila.RazonSocial, ambito.EsEmpresaCreadaFueraDelAmbito(empresaExistenteId)
                            ? AmbitoCargaMasiva.MotivoEmpresaCreadaFueraDeCartera(fila.RazonSocial)
                            : AmbitoCargaMasiva.Motivo("Esta empresa")));
                else if (!fila.YaExiste)
                    omitidosEnEscritura.Add(new ItemImportacionDto(
                        fila.Hoja, fila.Fila, fila.RazonSocial,
                        $"La empresa «{fila.RazonSocial}» ya existía al confirmar la importación, aunque no existía al analizar el archivo; se reutiliza la que ya había en vez de duplicarla."));
                continue;
            }

            // La razón social de una Empresa dada de baja (o activa, con otros espacios
            // exteriores) del Tenant propietario haría fallar el SaveChangesAsync final por
            // IX_Empresas_TenantId_RazonSocial y, con él, el archivo entero. Se omite solo
            // esta fila, con su causa; un return a mitad del plan dejaría en el DbContext del
            // circuito las altas ya añadidas.
            if (await DuplicadosEmpresa.ComprobarRazonSocialAsync(empresaRepositorio, fila.RazonSocial,
                cancellationToken: cancellationToken) is { } razonSocialOcupada)
            {
                omitidosEnEscritura.Add(new ItemImportacionDto(fila.Hoja, fila.Fila, fila.RazonSocial, razonSocialOcupada.Mensaje));
                continue;
            }

            // D-P11: cada fila de la hoja de Empresas crea una Empresa propia, y el Tenant propietario solo puede tener
            // una. Las que esta misma carga ya añadió cuentan aunque aún no estén guardadas.
            if (await limiteEmpresaPropia.ComprobarAltaAsync(empresasCreadas, esIntento: !limiteAvisado, cancellationToken) is { } limite)
            {
                omitidosEnEscritura.Add(new ItemImportacionDto(fila.Hoja, fila.Fila, fila.RazonSocial, limite.Mensaje));
                limiteAvisado = true;
                continue;
            }

            try
            {
                var empresa = new Empresa(fila.RazonSocial);
                empresaRepositorio.Agregar(empresa);
                empresasPorRazonSocial.Agregar(fila.RazonSocial, empresa.Id);
                empresasCreadas++;
                ambito.RegistrarEmpresaCreada(empresa.Id);
                if (ambito.AvisoEmpresaCreadaFueraDeCartera(empresa.Id, fila.RazonSocial) is { } aviso)
                    advertenciasEnEscritura.Add(new ItemImportacionDto(fila.Hoja, fila.Fila, fila.RazonSocial, aviso));
            }
            catch (ArgumentException ex)
            {
                omitidosEnEscritura.Add(new ItemImportacionDto(fila.Hoja, fila.Fila, fila.RazonSocial, ex.Message));
            }
        }

        foreach (var fila in plan.Trabajadores)
        {
            if (trabajadoresPorDni.TryGetValue(fila.Dni, out var trabajadorExistenteId))
            {
                // Reutilización anunciada por el análisis (ver nota de la clase). Fuera del ámbito se omite y se cuenta
                // con el motivo uniforme, sin decir que ya existía.
                if (!ambito.Trabajador(trabajadorExistenteId))
                    omitidosEnEscritura.Add(new ItemImportacionDto(
                        fila.Hoja, fila.Fila, $"{fila.Nombre} {fila.Apellidos} ({fila.Dni})",
                        AmbitoCargaMasiva.Motivo("Un trabajador con ese DNI")));
                else if (!fila.YaExiste)
                    omitidosEnEscritura.Add(new ItemImportacionDto(
                        fila.Hoja, fila.Fila, $"{fila.Nombre} {fila.Apellidos} ({fila.Dni})",
                        $"Ya existía un trabajador con el DNI {fila.Dni} al confirmar la importación, aunque no existía al analizar el archivo; se reutiliza el que ya había en vez de duplicarlo."));
                continue;
            }

            if (EstaEnPapelera(fila.Dni))
            {
                omitidosEnEscritura.Add(new ItemImportacionDto(
                    fila.Hoja, fila.Fila, $"{fila.Nombre} {fila.Apellidos} ({fila.Dni})", ambito.MotivoPapelera(fila.Dni)));
                continue;
            }

            var busquedaEmpresaDelTrabajador = empresasPorRazonSocial.Buscar(fila.RazonSocialEmpresa, out var empresaId);
            if (busquedaEmpresaDelTrabajador == ResultadoBusquedaPorNombre.Ambiguo)
            {
                omitidosEnEscritura.Add(new ItemImportacionDto(
                    fila.Hoja, fila.Fila, $"{fila.Nombre} {fila.Apellidos} ({fila.Dni})", MotivoVariasEmpresas(fila.RazonSocialEmpresa)));
                continue;
            }

            if (busquedaEmpresaDelTrabajador == ResultadoBusquedaPorNombre.NoExiste)
            {
                // Dependencia dentro del propio archivo: la Empresa del trabajador
                // ni existía antes ni pudo crearse en esta importación. Con ámbito acotado, el mismo motivo que una
                // Empresa fuera del ámbito: esta fila no intenta crear la Empresa que cita.
                omitidosEnEscritura.Add(new ItemImportacionDto(
                    fila.Hoja, fila.Fila, $"{fila.Nombre} {fila.Apellidos} ({fila.Dni})",
                    ambito.MotivoSinDistinguirExistencia(QuePideEmpresaDelTrabajador, EntidadDelAmbito.Empresa,
                        $"La empresa «{fila.RazonSocialEmpresa}» no existe y no pudo crearse al importar este archivo; un trabajador no puede darse de alta sin la empresa a la que pertenece.")));
                continue;
            }

            if (!ambito.Empresa(empresaId))
            {
                // La Empresa que crea la propia carga no entra en el ámbito (ver AmbitoCargaMasiva.RegistrarEmpresaCreada);
                // la fila lo dice en vez del motivo uniforme, que aquí no oculta nada.
                omitidosEnEscritura.Add(new ItemImportacionDto(
                    fila.Hoja, fila.Fila, $"{fila.Nombre} {fila.Apellidos} ({fila.Dni})",
                    ambito.EsEmpresaCreadaFueraDelAmbito(empresaId)
                        ? AmbitoCargaMasiva.MotivoEmpresaCreadaFueraDeCartera(fila.RazonSocialEmpresa)
                        : AmbitoCargaMasiva.Motivo(QuePideEmpresaDelTrabajador)));
                continue;
            }

            try
            {
                var trabajador = Trabajador.DeEmpresa(empresaId, fila.Nombre, fila.Apellidos, fila.Dni, fila.FechaNacimiento, fila.Email);
                trabajadorRepositorio.Agregar(trabajador);
                ambito.RegistrarTrabajadorCreado(trabajador.Id);
                trabajadoresNuevos.Add(trabajador);
                trabajadoresPorDni[fila.Dni] = trabajador.Id;
                trabajadoresCreados++;
            }
            catch (ArgumentException ex)
            {
                omitidosEnEscritura.Add(new ItemImportacionDto(fila.Hoja, fila.Fila, $"{fila.Nombre} {fila.Apellidos} ({fila.Dni})", ex.Message));
            }
        }

        var documentosExistentes = await documentosContext.Documentos.Operativos()
            .Select(d => new { d.TrabajadorId, d.TipoDocumentoId })
            .ToListAsync(cancellationToken);
        var clavesDocumentosExistentes = documentosExistentes
            .Select(d => (d.TrabajadorId, d.TipoDocumentoId))
            .ToHashSet();

        foreach (var fila in plan.Documentos)
        {
            if (!trabajadoresPorDni.TryGetValue(fila.Dni, out var trabajadorId))
            {
                // El documento se queda sin titular: su trabajador no existía ni
                // pudo crearse (su propia fila se omitió más arriba, con su motivo).
                // Si está en la Papelera, el motivo es ese: «no existe» sería falso. Con ámbito acotado, Papelera,
                // inexistencia y fuera de ámbito dan el mismo motivo: esta fila no intenta crear al trabajador.
                omitidosEnEscritura.Add(new ItemImportacionDto(
                    fila.Hoja, fila.Fila, $"{fila.Dni} — {fila.NombreTipoDocumento}",
                    EstaEnPapelera(fila.Dni)
                        ? ambito.MotivoPapeleraSinDistinguir("El trabajador de este documento", fila.Dni)
                        : ambito.MotivoTitularDeDocumentoInexistente(
                            $"El trabajador con DNI {fila.Dni} no existe y no pudo crearse al importar este archivo; su documento no tiene a quién asociarse.")));
                continue;
            }

            if (!ambito.Trabajador(trabajadorId))
            {
                omitidosEnEscritura.Add(new ItemImportacionDto(
                    fila.Hoja, fila.Fila, $"{fila.Dni} — {fila.NombreTipoDocumento}",
                    AmbitoCargaMasiva.Motivo("El trabajador de este documento")));
                continue;
            }

            var busquedaTipo = tiposDocumentoPorNombre.Buscar(fila.NombreTipoDocumento, out var tipoDocumento);
            if (busquedaTipo == ResultadoBusquedaPorNombre.Ambiguo)
            {
                omitidosEnEscritura.Add(new ItemImportacionDto(
                    fila.Hoja, fila.Fila, $"{fila.Dni} — {fila.NombreTipoDocumento}",
                    $"Hay varios tipos de documento llamados «{fila.NombreTipoDocumento}» en el catálogo (el nombre solo cambia en mayúsculas); la importación no puede saber a cuál se refiere. Renombra o elimina el duplicado en Tipos de documento y vuelve a importar."));
                continue;
            }

            if (busquedaTipo == ResultadoBusquedaPorNombre.NoExiste)
            {
                // Referencia a una entidad del catálogo que no existe: a diferencia
                // del caso anterior, este archivo nunca pudo crearla — el catálogo
                // de tipos de documento no se alimenta desde la importación.
                omitidosEnEscritura.Add(new ItemImportacionDto(
                    fila.Hoja, fila.Fila, $"{fila.Dni} — {fila.NombreTipoDocumento}",
                    $"El tipo de documento «{fila.NombreTipoDocumento}» no existe en el catálogo del sistema y la importación no lo crea; da de alta el tipo en Tipos de documento y vuelve a importar."));
                continue;
            }

            if (!clavesDocumentosExistentes.Add((trabajadorId, tipoDocumento!.Id)))
            {
                // Reutilización anunciada por el análisis (ver nota de la clase).
                if (!fila.YaExiste)
                    omitidosEnEscritura.Add(new ItemImportacionDto(
                        fila.Hoja, fila.Fila, $"{fila.Dni} — {fila.NombreTipoDocumento}",
                        $"El trabajador {fila.Dni} ya tenía un documento de tipo «{fila.NombreTipoDocumento}» al confirmar la importación, aunque no lo tenía al analizar el archivo; se conserva el que ya había en vez de duplicarlo."));
                continue;
            }

            try
            {
                // Misma regla que el resto de productores: el archivo no trae
                // vencimiento manual ni confirmación de «no caduca», así que
                // solo un tipo con vencimiento automático calcula fecha; en los
                // demás el documento entra sin vigencia confirmada.
                var vigencia = CalculadoraEstadoDocumento.ResolverVigencia(
                    tipoDocumento!.AplicaVencimientoAutomatico, tipoDocumento.VigenciaMeses,
                    fila.FechaEmision, fechaVencimientoManual: null, noCaducaConfirmado: false);
                var documento = Documento.DeTrabajador(trabajadorId, tipoDocumento.Id, fila.FechaEmision, vigencia);
                documentoRepositorio.Agregar(documento);
                documentosNuevos.Add(documento);
                documentosCreados++;
            }
            catch (ArgumentException ex)
            {
                omitidosEnEscritura.Add(new ItemImportacionDto(fila.Hoja, fila.Fila, $"{fila.Dni} — {fila.NombreTipoDocumento}", ex.Message));
            }
        }

        // Corre bajo el candado de las altas con acreditación, igual que CrearAsignacion y ReactivarAsignacion: las
        // Asignaciones que otra operación confirmó mientras esta esperaba cuentan como existentes (revisión puente de la
        // PR #2074, previa a la ronda 5, hallazgo 3). La lectura es una proyección sin rastreo, así que siempre trae lo
        // confirmado: no hace falta una lectura «…ActualizadoAsync».
        async Task AgregarAsignacionesAsync(CancellationToken ct)
        {
            // Trae TODAS las asignaciones existentes, activas o cerradas: DEC-19
            // exige comprobar solape de rango también contra las ya cerradas, no
            // solo si el par sigue activo — el hueco que
            // IX_Asignaciones_TenantId_TrabajadorId_CentroId_Activa nunca cubrió.
            var asignacionesExistentes = await asignacionesContext.Asignaciones
                .Select(a => new { a.TrabajadorId, a.CentroId, a.FechaAlta, a.FechaBaja })
                .ToListAsync(ct);
            var clavesAsignacionesActivas = asignacionesExistentes
                .Where(a => a.FechaBaja is null)
                .Select(a => (a.TrabajadorId, a.CentroId))
                .ToHashSet();
            // Mismo límite exclusivo que Asignacion.SeSolapaCon: la asignación
            // nueva es un rango abierto [hoy, ∞), así que solapa con una fila ya
            // cerrada exactamente cuando esa fila se cerró después de hoy. Un
            // rango vacío (FechaAlta == FechaBaja) no cuenta: no ocupó ni un día.
            var clavesAsignacionesSolapanConCerrada = asignacionesExistentes
                .Where(a => a.FechaBaja is not null && a.FechaBaja.Value != a.FechaAlta && hoy < a.FechaBaja.Value)
                .Select(a => (a.TrabajadorId, a.CentroId))
                .ToHashSet();

            // A diferencia de las demás hojas, el análisis NO deduplica pares
            // (trabajador, centro) repetidos dentro del propio archivo: la hoja
            // Asignaciones se recorre por filas y dos filas con el mismo nombre
            // completo producen el mismo par. Sin esta pista, la segunda se
            // confundiría con una asignación aparecida entre analizar y confirmar y
            // se le daría un motivo falso — dos causales distintas, un solo mensaje.
            var clavesAsignacionesDelPlan = new HashSet<(Guid, Guid)>();

            foreach (var fila in plan.Asignaciones)
            {
                // La hoja de Asignaciones no crea ni Trabajadores ni Centros: con ámbito acotado, un Trabajador o un Centro que
                // no existe (o un DNI en la Papelera) se omite con el mismo motivo que uno fuera del ámbito
                // (MotivoSinDistinguirExistencia). Las causales accionables de DCR-12 B se conservan con autoridad Total.
                if (!trabajadoresPorDni.TryGetValue(fila.Dni, out var trabajadorId))
                {
                    omitidosEnEscritura.Add(new ItemImportacionDto(
                        fila.Hoja, fila.Fila, $"{fila.Dni} — {fila.NombreCentro}",
                        EstaEnPapelera(fila.Dni)
                            ? ambito.MotivoPapeleraSinDistinguir(QuePideAsignacion, fila.Dni)
                            : ambito.MotivoSinDistinguirExistencia(QuePideAsignacion, EntidadDelAmbito.Trabajador,
                                $"El trabajador con DNI {fila.Dni} no existe y no pudo crearse al importar este archivo; sin él la asignación al centro «{fila.NombreCentro}» no puede crearse.")));
                    continue;
                }

                // Antes que las causales del Centro: un Trabajador fuera del ámbito con un Centro ambiguo o inexistente
                // recibiría un motivo distinto del de un DNI inexistente, y delataría que el DNI existe.
                if (!ambito.Trabajador(trabajadorId))
                {
                    omitidosEnEscritura.Add(new ItemImportacionDto(
                        fila.Hoja, fila.Fila, $"{fila.Dni} — {fila.NombreCentro}", AmbitoCargaMasiva.Motivo(QuePideAsignacion)));
                    continue;
                }

                var busquedaCentro = centrosPorNombre.Buscar(fila.NombreCentro, out var centroId);
                if (busquedaCentro == ResultadoBusquedaPorNombre.Ambiguo)
                {
                    // Esta plantilla no recoge el Cliente empresarial del Centro, así que no hay forma de elegir entre
                    // homónimos; asignar a uno cualquiera pondría al trabajador en el Centro equivocado.
                    omitidosEnEscritura.Add(new ItemImportacionDto(
                        fila.Hoja, fila.Fila, $"{fila.Dni} — {fila.NombreCentro}",
                        $"Hay varios centros llamados «{fila.NombreCentro}» en el sistema (por ejemplo, de distintos Clientes empresariales); esta plantilla no recoge el Cliente del centro y la importación no puede saber a cuál asignar al trabajador. Haz esta asignación manualmente desde el centro que corresponda."));
                    continue;
                }

                if (busquedaCentro == ResultadoBusquedaPorNombre.NoExiste)
                {
                    // DCR-12 B exige distinguir aquí la causal, no dar un motivo común.
                    // Son tres situaciones distintas y el usuario actúa distinto en cada
                    // una: el Centro que este archivo declaraba y no pudo crearse (Fase 10
                    // le exige una Empresa que la plantilla no recoge), el que el archivo
                    // ni siquiera menciona, y el que sí existía al analizar y ha dejado de
                    // existir antes de confirmar — a ese último no se le puede decir que
                    // "no pudo crearse", porque nunca se prometió crearlo.
                    var motivoCentro = centrosDeclaradosEnElArchivo.TryGetValue(fila.NombreCentro, out var existiaAlAnalizar)
                        ? existiaAlAnalizar
                            ? $"El centro «{fila.NombreCentro}» existía al analizar el archivo pero ya no existe al confirmar la importación; sin él la asignación no puede crearse."
                            : $"El centro «{fila.NombreCentro}» venía en la hoja Centros_Plataformas de este archivo pero no pudo crearse al importarlo (ahora requiere una Empresa asociada que esta plantilla no recoge); créalo manualmente en Centros y vuelve a importar para que esta asignación se registre."
                        : $"El centro «{fila.NombreCentro}» no existe en el sistema y este archivo no lo declara en la hoja Centros_Plataformas; sin él la asignación no puede crearse.";

                    omitidosEnEscritura.Add(new ItemImportacionDto(
                        fila.Hoja, fila.Fila, $"{fila.Dni} — {fila.NombreCentro}",
                        ambito.MotivoSinDistinguirExistencia(QuePideAsignacion, EntidadDelAmbito.Centro, motivoCentro)));
                    continue;
                }

                if (!ambito.Trabajador(trabajadorId) || !ambito.Centro(centroId))
                {
                    omitidosEnEscritura.Add(new ItemImportacionDto(
                        fila.Hoja, fila.Fila, $"{fila.Dni} — {fila.NombreCentro}",
                        AmbitoCargaMasiva.Motivo(QuePideAsignacion)));
                    continue;
                }

                var repetidaEnElArchivo = !clavesAsignacionesDelPlan.Add((trabajadorId, centroId));
                var clave = (trabajadorId, centroId);

                if (clavesAsignacionesActivas.Contains(clave))
                {
                    if (repetidaEnElArchivo)
                        // El propio archivo la trae dos veces (el plan prometió dos altas
                        // y solo cabe una): se registra para que los contadores cuadren,
                        // con su causal, no con la de la reaparición entre pasos.
                        omitidosEnEscritura.Add(new ItemImportacionDto(
                            fila.Hoja, fila.Fila, $"{fila.Dni} — {fila.NombreCentro}",
                            $"Esta asignación del trabajador {fila.Dni} al centro «{fila.NombreCentro}» ya venía antes en este mismo archivo; se registra una sola vez."));
                    else if (!fila.YaExiste)
                        // Reutilización que el análisis NO llegó a anunciar (ver nota de la clase).
                        omitidosEnEscritura.Add(new ItemImportacionDto(
                            fila.Hoja, fila.Fila, $"{fila.Dni} — {fila.NombreCentro}",
                            $"El trabajador {fila.Dni} ya estaba asignado al centro «{fila.NombreCentro}» al confirmar la importación, aunque no lo estaba al analizar el archivo; se conserva la asignación activa que ya había en vez de duplicarla."));
                    continue;
                }

                // DEC-19: la nueva alta no puede pisar el rango de una asignación
                // YA CERRADA del mismo trío — a diferencia del caso de arriba,
                // aquí no hay nada que "reutilizar"; es una contradicción de
                // datos y se omite con su propia causal, nunca en silencio
                // (DCR-12 B). Comprobado antes de intentar crear, no dentro del
                // catch de ArgumentException: el dominio no conoce el histórico,
                // solo el repositorio puede detectarlo.
                if (clavesAsignacionesSolapanConCerrada.Contains(clave))
                {
                    omitidosEnEscritura.Add(new ItemImportacionDto(
                        fila.Hoja, fila.Fila, $"{fila.Dni} — {fila.NombreCentro}",
                        $"El trabajador {fila.Dni} tuvo antes una asignación al centro «{fila.NombreCentro}» cuyo periodo de vigencia se solapa con la fecha de alta de hoy; revisa las fechas del historial antes de reintentar."));
                    continue;
                }

                try
                {
                    var asignacion = new Asignacion(trabajadorId, centroId, hoy);
                    asignacionRepositorio.Agregar(asignacion);
                    asignacionesNuevas.Add(asignacion);
                    // Marca el par como ocupado para que una fila repetida más
                    // adelante en este mismo archivo caiga en la rama de arriba
                    // ("ya venía antes en este mismo archivo") en vez de crear una
                    // segunda asignación activa para el mismo trío.
                    clavesAsignacionesActivas.Add(clave);
                    asignacionesCreadas++;
                }
                catch (ArgumentException ex)
                {
                    omitidosEnEscritura.Add(new ItemImportacionDto(fila.Hoja, fila.Fila, $"{fila.Dni} — {fila.NombreCentro}", ex.Message));
                }
            }
        }

        // Acreditaciones de plataforma de lo importado, con la misma regla que
        // el alta manual: cada Documento nuevo ante los Centros de su Trabajador
        // que exigen su tipo, y cada Asignación nueva para los Documentos del
        // Trabajador y de su Empresa que ese Centro exige. Una importación
        // repetida no crea Documentos ni Asignaciones nuevas, así que tampoco
        // duplica acreditaciones.
        //
        // Guarda TODO lo pendiente (Empresa/Trabajador/Documento/Asignación/
        // Acreditación de los bucles de arriba, junto con la fila de OperacionImportacion
        // agregada al principio) en un único SaveChangesAsync — así, si dos
        // confirmaciones de la MISMA operación compiten, la perdedora choca en
        // el índice único de OperacionImportacion y su transacción entera se
        // descarta: ninguna de sus filas llega a escribirse. Ver el comentario
        // de la clase y de OperacionImportacion.cs para el diseño completo.
        var yaEjecutada = false;
        async Task<Result> GuardarAsync(CancellationToken ct)
        {
            await altaAcreditaciones.AgregarPendientesAsync(new AltasConAcreditacion
            {
                Documentos = documentosNuevos,
                Asignaciones = asignacionesNuevas,
                Trabajadores = trabajadoresNuevos,
            }, ct);

            if (await operacionImportacionRepositorio.GuardarSiOperacionNuevaAsync(ct))
                return Result.Exito();

            // Fallo para que la transacción de la serialización se deshaga; el llamante lo traduce a «ya ejecutada».
            yaEjecutada = true;
            return Result.Fallo(Error.Crear("Importacion.YaEjecutada", "Esta importación ya se había ejecutado."));
        }

        // Una Asignación nueva pone en juego los Documentos de la Empresa del Trabajador en un Centro de otra Empresa:
        // se serializa con el alta de un Documento de Empresa, igual que CrearAsignacionCommand (revisión Codex de la
        // PR #2074, ronda 3, hallazgo 4). Un plan con Asignaciones toma el candado antes de leer las existentes, y lo
        // suelta al confirmar: dentro quedan esa lectura, el recorrido en memoria de la hoja, las acreditaciones y el
        // guardado; lo demás de la importación corre antes, sin candado. Un plan cuya hoja acaba sin ninguna Asignación
        // nueva (todas omitidas) también lo toma: solo se sabe después de leer. Lo importado ya está agregado al contexto,
        // así que la serialización no reintenta: un fallo transitorio falla la importación en vez de confirmar solo
        // una parte, y la hoja de Asignaciones no se recorre dos veces.
        if (plan.Asignaciones.Count > 0)
            await serializacionAltas.EjecutarAsync(async ct =>
            {
                await AgregarAsignacionesAsync(ct);
                return await GuardarAsync(ct);
            }, cancellationToken);
        else
            await GuardarAsync(cancellationToken);

        if (yaEjecutada)
            return ResultadoImportacionDto.YaEjecutada();

        return new ResultadoImportacionDto(
            clientesCreados, centrosCreados, empresasCreadas, trabajadoresCreados, documentosCreados, asignacionesCreadas,
            [.. plan.Advertencias, .. advertenciasEnEscritura], [.. plan.Omitidos, .. omitidosEnEscritura]);
    }

    /// <summary>Lo que pide una fila de Asignaciones: el mismo texto para «no existe», «Papelera» y «fuera de ámbito».</summary>
    private const string QuePideAsignacion = "El trabajador o el centro de esta asignación";

    /// <summary>Lo que pide una fila de Trabajadores a su Empresa: el mismo texto para «no existe» y «fuera de ámbito».</summary>
    private const string QuePideEmpresaDelTrabajador = "La empresa de este trabajador";

    private static string MotivoVariasEmpresas(string razonSocial) =>
        $"Hay varias empresas con la razón social «{razonSocial}» en el sistema (el nombre solo cambia en mayúsculas); la importación no puede saber a cuál se refiere esta fila. Unifica o renombra las empresas duplicadas y vuelve a importar.";
}
