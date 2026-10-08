using CaeManager.Application.Common;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Proyectos;
using CaeManager.Domain.RelacionesEmpresariales;
using CaeManager.Domain.Subcontratas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Domain.Vehiculos;
using CaeManager.Domain.Visitas;
using CaeManager.Infrastructure.MultiTenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.Persistence.Seed;

/// <summary>
/// Deja en el Tenant propietario Pizza Planet los datos de ejemplo de la maqueta «Páginas 360 nuevas»
/// (<see cref="CatalogoFichas360Demo"/>), para que la ficha construida y la maqueta enseñen las mismas entidades,
/// los mismos estados y el mismo número de filas.
///
/// <para>
/// <b>Va encima de la rama de Pizza Planet, no al lado.</b> Las Empresas, los Centros de Trabajo y la mayoría de los
/// Trabajadores de la maqueta ya los siembra <see cref="EscenariosDireccionDemoSeeder"/>: aquí no se recrea ninguno.
/// Se añade lo que falta (Trabajadores, Vehículos, Proyectos, Visita, verificaciones externas, documentación de
/// empresa, requisitos por Centro) y se corrige lo que la maqueta enseña distinto: la vigencia de documentos ya
/// sembrados, quién está asignado a qué Centro, el Nivel de servicio de la subcontrata y la Visita de Sede Sevilla.
/// Todo ello solo con esta clave activa; sin ella, la rama queda exactamente como la deja su siembra.
/// </para>
///
/// <para>
/// <b>Inerte por defecto.</b> Corre solo con <c>DatosPrueba:Activo</c> y <see cref="ClaveConfiguracion"/>; exige
/// además <see cref="EscenariosDireccionDemoSeeder.ClaveConfiguracion"/>, porque sin esa rama no hay sobre qué
/// sembrar, y en Producción lanza, igual que ella. Idempotente: una segunda ejecución no escribe nada.
/// </para>
///
/// <para>
/// <b>Frontera.</b> No toca RLS, autorización ni reglas de cumplimiento: escribe con el contexto de tráfico normal
/// dentro del <see cref="AmbitoTenantExplicito"/> del Tenant propietario, por las fábricas y los métodos del dominio.
/// Lo que la maqueta enseña y el modelo no sabe calcular todavía no se inventa: se siembra el dato más cercano y la
/// regla queda sin sembrar.
/// </para>
/// </summary>
public static class Fichas360DemoSeeder
{
    public const string ClaveConfiguracion = "DatosPrueba:Fichas360";

    public static bool EstaActiva(IConfiguration configuration) =>
        configuration.GetValue<bool>("DatosPrueba:Activo") && configuration.GetValue<bool>(ClaveConfiguracion);

    /// <summary>
    /// La guarda de esta siembra, aparte de <see cref="SeedAsync"/> para que el arranque la invoque antes de cualquier
    /// escritura. Lanza en Producción (comparte la contraseña pública de la demo local con la rama de la que depende)
    /// y lanza también si la clave está activa sin la de esa rama: una clave que se queda a medias y no dice nada
    /// deja al comparador de fidelidad mirando fichas vacías. Inerte si la clave no está activa.
    /// </summary>
    public static void RechazarEnProduccion(IConfiguration configuration, IHostEnvironment entorno)
    {
        if (!EstaActiva(configuration))
            return;

        if (entorno.IsProduction())
            throw new InvalidOperationException(
                $"{ClaveConfiguracion} no puede activarse en Producción: siembra datos de ejemplo de una maqueta sobre la demo local.");

        if (!configuration.GetValue<bool>(EscenariosDireccionDemoSeeder.ClaveConfiguracion))
            throw new InvalidOperationException(
                $"{ClaveConfiguracion} exige {EscenariosDireccionDemoSeeder.ClaveConfiguracion}: los datos de la maqueta se " +
                $"siembran encima de la rama «{CatalogoFichas360Demo.NombreTenant}» de esa siembra.");
    }

    public static async Task SeedAsync(
        CaeManagerDbContext dbContext,
        IConfiguration configuration,
        IHostEnvironment entorno,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        if (!EstaActiva(configuration))
            return;

        RechazarEnProduccion(configuration, entorno);

        var tenantPropietarioId = await dbContext.Tenants
            .Where(t => t.Nombre == CatalogoFichas360Demo.NombreTenant)
            .Select(t => (Guid?)t.Id)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw FaltaLaRama("no existe su Tenant propietario");

        Dictionary<string, Centro> centros;
        List<(Guid UsuarioId, Guid OperadorTenantId)> carteras;
        using (AmbitoTenantExplicito.Establecer(tenantPropietarioId))
        {
            centros = await dbContext.Centros.ToDictionaryAsync(c => c.Nombre, cancellationToken);
            if (!centros.TryGetValue(CatalogoFichas360Demo.SedeSevilla, out var sedeSevilla))
                throw FaltaLaRama($"no existe el Centro de Trabajo «{CatalogoFichas360Demo.SedeSevilla}»");

            await AsegurarCanalPorCorreoAsync(dbContext, sedeSevilla, cancellationToken);

            if (await dbContext.Vehiculos.AnyAsync(v => v.NumeroPlaca == CatalogoFichas360Demo.MatriculaCamionGrua, cancellationToken))
                return;

            // El catálogo de asignaciones no lleva filtro global de Tenant: se acota a mano a la posición de propietario.
            carteras = (await dbContext.AsignacionesCartera
                    .Where(c => c.PropietarioTenantId == tenantPropietarioId
                                && c.OperadorTenantId != tenantPropietarioId
                                && c.Estado == EstadoAsignacion.Vigente)
                    .Select(c => new { c.UsuarioId, c.OperadorTenantId })
                    .ToListAsync(cancellationToken))
                .Select(c => (c.UsuarioId, c.OperadorTenantId))
                .ToList();
        }

        var gestoraCaeId = await GestoraCaeDeLaMaquetaAsync(dbContext, carteras, cancellationToken);

        using (AmbitoTenantExplicito.Establecer(tenantPropietarioId))
        {
            var constructor = new Constructor(dbContext, centros, gestoraCaeId, DiaDeNegocio.Hoy());
            await constructor.CargarAsync(cancellationToken);
            constructor.Construir();

            // Un único guardado: o queda todo o no queda nada, y la marca de idempotencia (el Camión grúa) no puede
            // existir sin el resto.
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        logger.LogInformation(
            "Datos de la maqueta de las páginas 360 sembrados en {Tenant} (hoy de la maqueta: {HoyDeLaMaqueta}).",
            CatalogoFichas360Demo.NombreTenant, CatalogoFichas360Demo.HoyDeLaMaqueta);
    }

    private static InvalidOperationException FaltaLaRama(string motivo) =>
        new($"La siembra de {ClaveConfiguracion} debe correr después de EscenariosDireccionDemoSeeder: en " +
            $"«{CatalogoFichas360Demo.NombreTenant}» {motivo}.");

    /// <summary>
    /// La persona que en la maqueta registra las verificaciones («por Marta Villalba»): la Gestora CAE del Operador CAE
    /// externo con Asignación de Cartera vigente en este Tenant propietario. Se llega a ella por su cartera, que es lo
    /// que la habilita, y su cuenta se lee en el Tenant del Operador CAE, que es donde vive (RLS de las cuentas).
    /// </summary>
    private static async Task<Guid> GestoraCaeDeLaMaquetaAsync(
        CaeManagerDbContext dbContext, List<(Guid UsuarioId, Guid OperadorTenantId)> carteras, CancellationToken cancellationToken)
    {
        var operadores = carteras.Select(c => c.OperadorTenantId).Distinct().ToList();
        if (operadores.Count != 1)
            throw FaltaLaRama($"hay {operadores.Count} Operadores CAE externos con cartera vigente y la rama deja exactamente uno");

        var conCartera = carteras.Select(c => c.UsuarioId).Distinct().ToList();
        using (AmbitoTenantExplicito.Establecer(operadores[0]))
        {
            return await dbContext.Users
                .Where(u => conCartera.Contains(u.Id) && u.Email == EscenariosDireccionDemoSeeder.EmailGestorPrimero)
                .Select(u => (Guid?)u.Id)
                .SingleOrDefaultAsync(cancellationToken)
                ?? throw FaltaLaRama($"{EscenariosDireccionDemoSeeder.EmailGestorPrimero} no tiene Asignación de Cartera vigente");
        }
    }

    /// <summary>
    /// El canal principal de Sede Sevilla pasa a ser el correo de accesos; el de plataforma que deja la siembra de
    /// base se conserva como secundario. En dos guardados porque el índice único admite un solo principal por Centro
    /// y no hay orden garantizado entre quitar la marca y ponerla dentro de uno; cada paso es idempotente por separado.
    /// </summary>
    private static async Task AsegurarCanalPorCorreoAsync(
        CaeManagerDbContext dbContext, Centro sedeSevilla, CancellationToken cancellationToken)
    {
        var canales = await dbContext.CanalesGestionDocumental.Where(c => c.CentroId == sedeSevilla.Id).ToListAsync(cancellationToken);
        if (canales.Any(c => c.Tipo == TipoCanalGestion.Email))
            return;

        foreach (var principal in canales.Where(c => c.EsPrincipal))
            principal.DejarDeSerPrincipal();
        await dbContext.SaveChangesAsync(cancellationToken);

        var porCorreo = CanalGestionDocumental.PorEmail(
            sedeSevilla.Id, CatalogoFichas360Demo.EtiquetaCanalPorCorreo, CatalogoFichas360Demo.CorreoDeAccesosSedeSevilla, nombreContacto: null);
        porCorreo.MarcarComoPrincipal();
        dbContext.CanalesGestionDocumental.Add(porCorreo);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Carga lo que la rama ya tiene y construye, sin guardar, todo lo de la maqueta. Una clase y no un método para
    /// que los diccionarios por nombre no viajen por una docena de parámetros.
    /// </summary>
    private sealed class Constructor(
        CaeManagerDbContext dbContext, IReadOnlyDictionary<string, Centro> centros, Guid gestoraCaeId, DateOnly hoy)
    {
        private Dictionary<string, Empresa> _empresas = [];
        private Dictionary<string, Trabajador> _trabajadores = [];
        private Dictionary<string, TipoDocumento> _tipos = [];
        private Dictionary<(Guid TrabajadorId, Guid TipoDocumentoId), Documento> _documentosDeTrabajador = [];
        private List<Asignacion> _asignacionesActivas = [];
        private Dictionary<(Guid TipoDocumentoId, Guid CentroId), TipoDocumentoCentro> _requisitos = [];
        private List<CanalGestionDocumental> _canalesDePlataforma = [];
        private List<Visita> _visitasDeSedeSevilla = [];
        private List<VisitaTrabajador> _asistentes = [];
        private Guid _relacionDeRaccoonConUmbrellaId;

        /// <summary>El día sembrado que corresponde a un desplazamiento de la maqueta.</summary>
        private DateOnly En(int desplazamiento) => hoy.AddDays(desplazamiento);

        public async Task CargarAsync(CancellationToken cancellationToken)
        {
            string[] razonesSociales =
            [
                CatalogoFichas360Demo.Cyberdyne, CatalogoFichas360Demo.Skynet, CatalogoFichas360Demo.Terminator,
                CatalogoFichas360Demo.Umbrella, CatalogoFichas360Demo.Raccoon, CatalogoFichas360Demo.Nemesis
            ];
            _empresas = await dbContext.Empresas
                .Where(e => razonesSociales.Contains(e.RazonSocial))
                .ToDictionaryAsync(e => e.RazonSocial, cancellationToken);
            if (_empresas.Count != razonesSociales.Length)
                throw FaltaLaRama("faltan Empresas de sus dos Clientes empresariales");

            var raccoonId = _empresas[CatalogoFichas360Demo.Raccoon].Id;
            var umbrellaId = _empresas[CatalogoFichas360Demo.Umbrella].Id;
            _relacionDeRaccoonConUmbrellaId = await dbContext.RelacionesEmpresariales
                .Where(r => r.ProveedoraId == raccoonId && r.ClienteId == umbrellaId)
                .Select(r => r.Id)
                .SingleAsync(cancellationToken);

            _trabajadores = (await dbContext.Trabajadores.ToListAsync(cancellationToken)).ToDictionary(t => t.NombreCompleto);
            _tipos = await dbContext.TiposDocumento.ToDictionaryAsync(t => t.Nombre, cancellationToken);
            _documentosDeTrabajador = (await dbContext.Documentos.Operativos()
                    .Where(d => d.TrabajadorId != null)
                    .ToListAsync(cancellationToken))
                .GroupBy(d => (d.TrabajadorId!.Value, d.TipoDocumentoId))
                .ToDictionary(g => g.Key, g => g.First());
            _asignacionesActivas = await dbContext.Asignaciones.Where(a => a.FechaBaja == null).ToListAsync(cancellationToken);
            _requisitos = (await dbContext.TiposDocumentoCentros.ToListAsync(cancellationToken))
                .ToDictionary(r => (r.TipoDocumentoId, r.CentroId));
            _canalesDePlataforma = await dbContext.CanalesGestionDocumental
                .Where(c => c.Tipo == TipoCanalGestion.Plataforma)
                .ToListAsync(cancellationToken);

            var sedeSevillaId = centros[CatalogoFichas360Demo.SedeSevilla].Id;
            _visitasDeSedeSevilla = await dbContext.Visitas.Where(v => v.CentroId == sedeSevillaId).ToListAsync(cancellationToken);
            var visitaIds = _visitasDeSedeSevilla.Select(v => v.Id).ToList();
            _asistentes = await dbContext.VisitasTrabajadores.Where(a => visitaIds.Contains(a.VisitaId)).ToListAsync(cancellationToken);
        }

        public void Construir()
        {
            AjustarSubcontrata();
            CrearTrabajadoresNuevos();
            AjustarAsignaciones();
            AjustarDocumentosDeTrabajador();
            AjustarRequisitosDeCentro();
            AcreditarEpiDeOscarFerrer();
            CrearDocumentosDeEmpresa();
            CrearVehiculos();
            CrearProyectos();
            AjustarVisitas();
            CrearVerificacionesExternas();
        }

        private Centro CentroDe(string nombre) =>
            centros.TryGetValue(nombre, out var centro) ? centro : throw FaltaLaRama($"no existe el Centro de Trabajo «{nombre}»");

        private Trabajador TrabajadorDe(string nombreCompleto) =>
            _trabajadores.TryGetValue(nombreCompleto, out var trabajador)
                ? trabajador
                : throw FaltaLaRama($"no existe el Trabajador «{nombreCompleto}»");

        private TipoDocumento TipoDe(string nombre) =>
            _tipos.TryGetValue(nombre, out var tipo)
                ? tipo
                : throw new InvalidOperationException($"El catálogo de «{CatalogoFichas360Demo.NombreTenant}» no tiene el Tipo de documento «{nombre}».");

        private VigenciaDocumento VigenciaDe(VigenciaMaqueta vigencia) => vigencia.Clase switch
        {
            ClaseVigenciaMaqueta.VenceEl => VigenciaDocumento.VenceEl(En(vigencia.VenceEn!.Value)),
            ClaseVigenciaMaqueta.SinConfirmar => VigenciaDocumento.SinConfirmar,
            ClaseVigenciaMaqueta.NoCaduca => VigenciaDocumento.NoCaduca,
            _ => throw new ArgumentOutOfRangeException(nameof(vigencia), vigencia.Clase, "Un documento ausente no tiene vigencia que sembrar.")
        };

        /// <summary>
        /// Transportes Terminator S.L. pasa a Supervisada (el ejemplo por defecto de la maqueta) y trabaja también para
        /// Umbrella Corporation Ibérica S.A., dentro de la relación de Limpiezas Raccoon S.L. con ese Cliente empresarial:
        /// sin esa Relación Empresarial no podría verificarse nada suyo en Planta Bilbao.
        /// </summary>
        private void AjustarSubcontrata()
        {
            var terminator = _empresas[CatalogoFichas360Demo.Terminator];
            terminator.CambiarNivelServicioComoSubcontrata(NivelServicioSubcontrata.Supervisada.ToString());

            dbContext.RelacionesEmpresariales.Add(RelacionEmpresarial.Crear(
                terminator.Id, _empresas[CatalogoFichas360Demo.Umbrella].Id, DateTime.UtcNow, _relacionDeRaccoonConUmbrellaId));
        }

        /// <summary>Con la misma documentación estándar, al día, que la siembra de base da a cada Trabajador.</summary>
        private void CrearTrabajadoresNuevos()
        {
            var estandar = DatosPruebaSeeder.DocumentacionEstandarTrabajador
                .Concat(DatosPruebaSeeder.DocumentacionObligatoriaSinVencimientoTrabajador)
                .ToList();
            var alDia = hoy.AddDays(VigenciaDemo.AlDia.DiasHastaVencimiento());

            foreach (var nuevo in CatalogoFichas360Demo.TrabajadoresNuevos)
            {
                var empleadorId = _empresas[nuevo.Empleador].Id;
                var trabajador = nuevo.DeSubcontrata
                    ? Trabajador.DeSubcontrata(empleadorId, nuevo.Nombre, nuevo.Apellidos, nuevo.Identificacion)
                    : Trabajador.DeEmpresa(empleadorId, nuevo.Nombre, nuevo.Apellidos, nuevo.Identificacion);
                dbContext.Trabajadores.Add(trabajador);
                _trabajadores[nuevo.NombreCompleto] = trabajador;

                foreach (var nombreTipo in estandar)
                {
                    var tipo = TipoDe(nombreTipo);
                    var documento = tipo.AplicaVencimientoAutomatico
                        ? Documento.DeTrabajador(
                            trabajador.Id, tipo.Id, alDia.AddMonths(-(tipo.VigenciaMeses ?? 12)), VigenciaDocumento.VenceEl(alDia))
                        : Documento.DeTrabajador(trabajador.Id, tipo.Id, hoy.AddDays(-120), VigenciaDocumento.NoCaduca);
                    dbContext.Documentos.Add(documento);
                    _documentosDeTrabajador[(trabajador.Id, tipo.Id)] = documento;
                }
            }
        }

        private void AjustarAsignaciones()
        {
            foreach (var baja in CatalogoFichas360Demo.Bajas)
            {
                var (trabajadorId, centroId) = (TrabajadorDe(baja.Trabajador).Id, CentroDe(baja.Centro).Id);
                var asignacion = _asignacionesActivas.SingleOrDefault(a => a.TrabajadorId == trabajadorId && a.CentroId == centroId)
                    ?? throw FaltaLaRama($"«{baja.Trabajador}» no está asignado a «{baja.Centro}»");

                // Nunca antes de su alta: la de base es relativa a hoy y la de la maqueta también.
                var fecha = En(baja.BajaEn);
                asignacion.DarDeBaja(fecha < asignacion.FechaAlta ? asignacion.FechaAlta : fecha);
            }

            foreach (var alta in CatalogoFichas360Demo.Asignaciones)
            {
                var asignacion = new Asignacion(TrabajadorDe(alta.Trabajador).Id, CentroDe(alta.Centro).Id, En(alta.AltaEn));
                if (alta.BajaEn is { } bajaEn)
                    asignacion.DarDeBaja(En(bajaEn));
                dbContext.Asignaciones.Add(asignacion);
            }
        }

        /// <summary>
        /// Un documento que ya existe se corrige en su sitio (conserva su Id y su acreditación en plataforma); uno que
        /// la maqueta enseña ausente se retira; uno que no existe se crea.
        /// </summary>
        private void AjustarDocumentosDeTrabajador()
        {
            foreach (var spec in CatalogoFichas360Demo.DocumentosDeTrabajador)
            {
                var (trabajador, tipo) = (TrabajadorDe(spec.Titular), TipoDe(spec.Tipo));
                var existe = _documentosDeTrabajador.TryGetValue((trabajador.Id, tipo.Id), out var documento);

                if (spec.Vigencia.Clase == ClaseVigenciaMaqueta.Ausente)
                {
                    if (!existe) continue;

                    if (dbContext.Entry(documento!).State == EntityState.Added)
                        dbContext.Documentos.Remove(documento!);
                    else
                        documento!.MarcarComoEliminado(gestoraCaeId);
                    _documentosDeTrabajador.Remove((trabajador.Id, tipo.Id));
                    continue;
                }

                var (emision, vigencia) = (En(spec.Vigencia.EmitidoEn), VigenciaDe(spec.Vigencia));
                if (existe)
                {
                    documento!.CorregirVigencia(emision, vigencia);
                    continue;
                }

                var nuevo = Documento.DeTrabajador(trabajador.Id, tipo.Id, emision, vigencia);
                dbContext.Documentos.Add(nuevo);
                _documentosDeTrabajador[(trabajador.Id, tipo.Id)] = nuevo;
            }
        }

        private void AjustarRequisitosDeCentro()
        {
            foreach (var spec in CatalogoFichas360Demo.Requisitos)
            {
                var (tipoId, centroId) = (TipoDe(spec.Tipo).Id, CentroDe(spec.Centro).Id);
                if (_requisitos.TryGetValue((tipoId, centroId), out var fila))
                {
                    fila.Actualizar(
                        spec.Incluido, spec.PeriodicidadMeses, spec.BloqueaAcceso,
                        fila.ArchivoUrl, fila.NombreArchivoOriginal, spec.ToleranciaDias);
                    continue;
                }

                dbContext.TiposDocumentoCentros.Add(new TipoDocumentoCentro(
                    tipoId, centroId, spec.Incluido, spec.PeriodicidadMeses, spec.BloqueaAcceso, toleranciaDias: spec.ToleranciaDias));
            }

            dbContext.ToleranciasDocumentoClienteEmpresarial.Add(new ToleranciaDocumentoClienteEmpresarial(
                _empresas[CatalogoFichas360Demo.Cyberdyne].Id, TipoDe(CatalogoFichas360Demo.EntregaEpi).Id,
                CatalogoFichas360Demo.ToleranciaEpiDeCyberdyne));
        }

        /// <summary>
        /// La Entrega de EPI de Óscar Ferrer Pons está aceptada en la plataforma de Planta Murcia y de Planta Bilbao
        /// (la de Almacén Vigo ya la deja la siembra de base) y sin subir a la de Sede Sevilla.
        /// </summary>
        private void AcreditarEpiDeOscarFerrer()
        {
            var documento = _documentosDeTrabajador[
                (TrabajadorDe("Óscar Ferrer Pons").Id, TipoDe(CatalogoFichas360Demo.EntregaEpi).Id)];

            foreach (var nombreCentro in new[] { CatalogoFichas360Demo.PlantaMurcia, CatalogoFichas360Demo.PlantaBilbao })
            {
                var centroId = CentroDe(nombreCentro).Id;
                if (_canalesDePlataforma.FirstOrDefault(c => c.CentroId == centroId) is not { } canal)
                    continue;

                var acreditacion = new AcreditacionDocumentoPlataforma(documento.Id, canal.Id);
                acreditacion.MarcarSubida();
                acreditacion.MarcarAceptada(VigenciaEnPlataforma.VenceEl(hoy.AddMonths(6)));
                dbContext.AcreditacionesDocumentoPlataforma.Add(acreditacion);
            }
        }

        private void CrearDocumentosDeEmpresa()
        {
            foreach (var spec in CatalogoFichas360Demo.DocumentosDeEmpresa)
                dbContext.Documentos.Add(Documento.DeEmpresa(
                    _empresas[spec.Titular].Id, TipoDe(spec.Tipo).Id, En(spec.Vigencia.EmitidoEn), VigenciaDe(spec.Vigencia)));
        }

        private void CrearVehiculos()
        {
            foreach (var spec in CatalogoFichas360Demo.Vehiculos)
            {
                var empleadorId = _empresas[spec.Empleador].Id;
                var vehiculo = spec.DeSubcontrata
                    ? Vehiculo.DeSubcontrata(empleadorId, spec.Nombre, spec.Modelo, spec.Matricula)
                    : Vehiculo.DeEmpresa(empleadorId, spec.Nombre, spec.Modelo, spec.Matricula);
                dbContext.Vehiculos.Add(vehiculo);

                foreach (var documentoSpec in CatalogoFichas360Demo.DocumentosDeVehiculo.Where(d => d.Titular == spec.Nombre))
                {
                    var tipo = TipoDe(documentoSpec.Tipo);
                    var documento = Documento.DeVehiculo(
                        vehiculo.Id, tipo.Id, En(documentoSpec.Vigencia.EmitidoEn), VigenciaDe(documentoSpec.Vigencia));
                    dbContext.Documentos.Add(documento);

                    if (spec.Nombre != CatalogoFichas360Demo.CamionGrua || documentoSpec.Tipo != CatalogoFichas360Demo.InspeccionTecnica)
                        continue;

                    // La inspección anterior queda en el historial, sustituida por la vigente.
                    var anterior = CatalogoFichas360Demo.InspeccionAnteriorDelCamionGrua;
                    var sustituido = Documento.DeVehiculo(vehiculo.Id, tipo.Id, En(anterior.EmitidoEn), VigenciaDe(anterior));
                    sustituido.SustituirPor(documento, MotivoSustitucionDocumento.Renovacion, DateTime.UtcNow);
                    dbContext.Documentos.Add(sustituido);
                }
            }
        }

        private void CrearProyectos()
        {
            for (var i = 0; i < CatalogoFichas360Demo.TiposDeProyecto.Count; i++)
            {
                var spec = CatalogoFichas360Demo.TiposDeProyecto[i];
                var tipo = new TipoDocumento(
                    spec.Nombre, spec.VigenciaMeses, aplicaVencimientoAutomatico: spec.VigenciaMeses is not null, orden: 900 + i,
                    AmbitoAplicacion.Proyecto, sensibilidad: SensibilidadDocumental.SinDatosPersonales);
                dbContext.TiposDocumento.Add(tipo);
                _tipos[spec.Nombre] = tipo;
            }

            foreach (var spec in CatalogoFichas360Demo.Proyectos)
            {
                var centro = CentroDe(spec.Centro);
                var proyecto = Proyecto.Crear(
                    _empresas[spec.ClienteEmpresarial].Id, centro.Id, spec.Nombre, En(spec.InicioEn), En(spec.FinPrevistoEn), spec.Notas);
                dbContext.Proyectos.Add(proyecto);

                foreach (var tecnicoSpec in CatalogoFichas360Demo.Tecnicos.Where(t => t.Proyecto == spec.Nombre))
                {
                    var tecnico = new ProyectoTecnico(proyecto.Id, TrabajadorDe(tecnicoSpec.Trabajador).Id, En(tecnicoSpec.AltaEn));
                    if (tecnicoSpec.BajaEn is { } bajaEn)
                        tecnico.DarDeBaja(En(bajaEn));
                    dbContext.ProyectosTecnicos.Add(tecnico);
                }

                foreach (var documentoSpec in CatalogoFichas360Demo.DocumentosDeProyecto.Where(d => d.Titular == spec.Nombre))
                    dbContext.Documentos.Add(Documento.DeProyecto(
                        proyecto.Id, TipoDe(documentoSpec.Tipo).Id, En(documentoSpec.Vigencia.EmitidoEn), VigenciaDe(documentoSpec.Vigencia)));
            }
        }

        /// <summary>
        /// La Visita que la siembra de base deja en Sede Sevilla pasa a ser la ya finalizada de la maqueta (otra fecha y
        /// un solo Trabajador) y se añade la que termina hoy.
        /// </summary>
        private void AjustarVisitas()
        {
            var finalizadaSpec = CatalogoFichas360Demo.VisitaFinalizada;
            var finalizada = _visitasDeSedeSevilla.FirstOrDefault();
            if (finalizada is null)
            {
                finalizada = NuevaVisita(finalizadaSpec);
            }
            else
            {
                finalizada.Actualizar(En(finalizadaSpec.DesdeEn), En(finalizadaSpec.HastaEn), notas: null, finalizadaSpec.Entrada);

                var quedan = finalizadaSpec.Trabajadores.Select(nombre => TrabajadorDe(nombre).Id).ToHashSet();
                var actuales = _asistentes.Where(a => a.VisitaId == finalizada.Id).ToList();
                dbContext.VisitasTrabajadores.RemoveRange(actuales.Where(a => !quedan.Contains(a.TrabajadorId)));
                foreach (var falta in quedan.Except(actuales.Select(a => a.TrabajadorId)))
                    dbContext.VisitasTrabajadores.Add(new VisitaTrabajador(finalizada.Id, falta));
            }

            NuevaVisita(CatalogoFichas360Demo.VisitaEnCurso);
        }

        private Visita NuevaVisita(VisitaMaqueta spec)
        {
            var visita = new Visita(CentroDe(spec.Centro).Id, En(spec.DesdeEn), En(spec.HastaEn), notas: null, spec.Origen, spec.Entrada);
            dbContext.Visitas.Add(visita);
            foreach (var nombre in spec.Trabajadores)
                dbContext.VisitasTrabajadores.Add(new VisitaTrabajador(visita.Id, TrabajadorDe(nombre).Id));
            return visita;
        }

        private void CrearVerificacionesExternas()
        {
            var terminatorId = _empresas[CatalogoFichas360Demo.Terminator].Id;
            foreach (var spec in CatalogoFichas360Demo.Verificaciones)
                dbContext.VerificacionesExternaSubcontrata.Add(new VerificacionExternaSubcontrata(
                    terminatorId, CentroDe(spec.Centro).Id, TipoDe(spec.Tipo).Id, En(spec.ComprobadaEn),
                    spec.Valido ? ResultadoVerificacionExterna.Valido : ResultadoVerificacionExterna.NoValido,
                    gestoraCaeId,
                    spec.ValidoHastaEn is { } hasta ? En(hasta) : null));
        }
    }
}
