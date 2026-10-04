using CaeManager.Application.Bandeja.Queries.ObtenerBandejaAgrupada;
using CaeManager.Application.Bandeja.Queries.ObtenerBandejaGestor;
using CaeManager.Application.Centros;
using CaeManager.Application.Centros.Queries.ObtenerCentros;
using CaeManager.Application.Centros.Queries.ObtenerDocumentacionBloqueantePendiente;
using CaeManager.Application.Common;
using CaeManager.Application.DependencyInjection;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using MediatR;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaeManager.IntegrationTests.Coherencia;

/// <summary>
/// La «foto» de las superficies que reaccionan al veredicto de la plataforma del Cliente empresarial (vigencia vencida allí y
/// acreditación Rechazada, D-7), sobre un Tenant propio que mezcla los casos que la regla distingue: el sujeto (Trabajador o
/// Empresa), el Centro de la plataforma, la asignación vigente o dada de baja, el tipo aplicable o no, y la marca de bloqueo
/// documental del Centro. Misma idea que <see cref="EscenarioDeFotoDeSuperficies"/>: cada sección es una lista de líneas
/// ordenada y escrita con NOMBRES, para compararla entre dos árboles de código y explicar cada diferencia por la regla.
///
/// <para>
/// Es un Tenant aparte a propósito: la foto de <see cref="EscenarioDeFotoDeSuperficies"/> está atada a oráculos escritos a mano
/// (<c>LaToleranciaNoCambiaEnSilencioLosRegistrosTests</c>) y no debe cambiar con esto. La foto «antes» (sobre <c>origin/main</c>)
/// y la «después» se tomaron con este mismo escenario: la comparación va en el cuerpo de la PR, y el oráculo del resultado
/// actual lo fija <c>LaPlataformaBloqueaAlTrabajadorYNoAlCentroTests</c>.
/// </para>
/// </summary>
internal sealed class EscenarioDeFotoDePlataforma : IAsyncDisposable
{
    private const string LetrasDni = "TRWAGMYFPDXBNJZSQVHLCKE";

    private readonly string _cadenaConexion;
    private readonly DateOnly _hoy = DiaDeNegocio.Hoy();
    private int _dni = 20_000_000;
    private ServiceProvider? _servicios;
    private CaeManagerDbContext? _contextoDeLaCola;

    public EscenarioDeFotoDePlataforma(string cadenaConexion) => _cadenaConexion = cadenaConexion;

    public Guid Tenant { get; } = Guid.NewGuid();

    // Nombres canónicos para que los oráculos y la PR hablen de lo mismo.
    public const string CentroP1 = "Centro P1";
    public const string CentroP2 = "Centro P2";
    public const string CentroP3 = "Centro P3";
    public const string CentroP4 = "Centro P4";
    public const string CentroP5 = "Centro P5";
    public const string CentroP6 = "Centro P6";

    public CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = Tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;
        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private string NuevoDni() => $"{++_dni:D8}{LetrasDni[_dni % 23]}";

    private Guid Alta(CaeManagerDbContext c, Empresa empresa, string apellidos, params Centro[] centros)
    {
        var t = Trabajador.DeEmpresa(empresa.Id, "Caso", apellidos, NuevoDni());
        c.Trabajadores.Add(t);
        c.SaveChanges();
        foreach (var centro in centros)
            c.Asignaciones.Add(new Asignacion(t.Id, centro.Id, _hoy.AddDays(-400)));
        c.SaveChanges();
        return t.Id;
    }

    private static Guid Acreditar(
        CaeManagerDbContext c, Documento documento, CanalGestionDocumental canal, Action<AcreditacionDocumentoPlataforma> estado)
    {
        var acreditacion = new AcreditacionDocumentoPlataforma(documento.Id, canal.Id);
        estado(acreditacion);
        c.AcreditacionesDocumentoPlataforma.Add(acreditacion);
        c.SaveChanges();
        return acreditacion.Id;
    }

    private static void Rechazada(AcreditacionDocumentoPlataforma a) =>
        a.Rechazar(CausaRechazoAcreditacion.Otro, "Documento ilegible", DateTime.UtcNow);

    /// <summary>
    /// Siembra el Tenant. Cliente empresarial Z: Centros P1, P2 y P4; Cliente empresarial W: Centro P3. El PSS (Trabajador,
    /// requerido) y el Certificado (Empresa, requerido) son aplicables explícitamente en P1, P2 y P4; en P4 el PSS es además
    /// bloqueante documental. «Opcional» es un tipo de Trabajador no requerido y sin fila en ningún Centro: no aplica.
    /// </summary>
    public async Task SembrarAsync()
    {
        await using var migracion = CrearContexto();
        await migracion.Database.MigrateAsync();

        await using var c = CrearContexto();
        var parametros = c.ParametrosSistema.SingleOrDefault();
        if (parametros is null) c.ParametrosSistema.Add(new ParametroSistema(30, 15));
        c.SaveChanges();

        var clienteZ = Empresa.CrearComoCliente("Cliente Z", "B12345674", false, null, null);
        var clienteW = Empresa.CrearComoCliente("Cliente W", "B87654323", false, null, null);
        var contratista = new Empresa("Contratista");
        var empresaE1 = new Empresa("Empresa E1");
        var empresaE2 = new Empresa("Empresa E2");
        var empresaE3 = new Empresa("Empresa E3");
        c.Empresas.AddRange(clienteZ, clienteW, contratista, empresaE1, empresaE2, empresaE3);
        c.SaveChanges();

        var p1 = new Centro(clienteZ.Id, contratista.Id, CentroP1);
        var p2 = new Centro(clienteZ.Id, contratista.Id, CentroP2);
        var p3 = new Centro(clienteW.Id, contratista.Id, CentroP3);
        var p4 = new Centro(clienteZ.Id, contratista.Id, CentroP4);
        var p5 = new Centro(clienteW.Id, contratista.Id, CentroP5);
        var p6 = new Centro(clienteW.Id, contratista.Id, CentroP6);
        c.Centros.AddRange(p1, p2, p3, p4, p5, p6);

        var pss = new TipoDocumento("PSS firmado", null, aplicaVencimientoAutomatico: false, 1, AmbitoAplicacion.Trabajador, requerido: RequisitoDocumental.Si);
        var certificado = new TipoDocumento("Certificado SS", null, aplicaVencimientoAutomatico: false, 2, AmbitoAplicacion.Empresa, requerido: RequisitoDocumental.Si);
        var opcional = new TipoDocumento("Opcional", null, aplicaVencimientoAutomatico: false, 3, AmbitoAplicacion.Trabajador, requerido: RequisitoDocumental.No);
        c.TiposDocumento.AddRange(pss, certificado, opcional);
        c.SaveChanges();

        foreach (var centro in new[] { p1, p2, p3, p4, p5, p6 })
        {
            c.TiposDocumentoCentros.Add(new TipoDocumentoCentro(pss.Id, centro.Id, incluido: true, bloqueaAcceso: centro.Id == p4.Id));
            c.TiposDocumentoCentros.Add(new TipoDocumentoCentro(certificado.Id, centro.Id, incluido: true));
        }
        c.SaveChanges();

        var proveedor = new ProveedorPlataformaCae("PLAT-FOTO", "Plataforma de la foto");
        c.ProveedoresPlataformaCae.Add(proveedor);
        c.SaveChanges();
        CanalGestionDocumental Canal(Centro centro)
        {
            var canal = CanalGestionDocumental.DePlataforma(centro.Id, "Acceso de la foto", proveedor.Id, null, null, null);
            c.CanalesGestionDocumental.Add(canal);
            c.SaveChanges();
            return canal;
        }
        var canalP1 = Canal(p1);
        var canalP3 = Canal(p3);
        var canalP4 = Canal(p4);
        var canalP5 = Canal(p5);
        var canalP6 = Canal(p6);
        var emision = _hoy.AddDays(-400);
        var vigenteEnTalveg = VigenciaDocumento.VenceEl(_hoy.AddDays(200));

        Documento DeTrabajador(Guid trabajadorId, TipoDocumento tipo)
        {
            var documento = Documento.DeTrabajador(trabajadorId, tipo.Id, emision, vigenteEnTalveg);
            c.Documentos.Add(documento);
            c.SaveChanges();
            return documento;
        }

        Documento DeEmpresa(Empresa empresa, TipoDocumento tipo)
        {
            var documento = Documento.DeEmpresa(empresa.Id, tipo.Id, emision, vigenteEnTalveg);
            c.Documentos.Add(documento);
            c.SaveChanges();
            return documento;
        }

        // --- Sujeto Trabajador, canal de P1 ------------------------------------------------------------------------------
        var vencida = Alta(c, empresaE1, "w vencida en plataforma", p1);
        Acreditar(c, DeTrabajador(vencida, pss), canalP1, a => a.MarcarAceptada(VigenciaEnPlataforma.VenceEl(_hoy.AddDays(-1))));

        var venceHoy = Alta(c, empresaE1, "w vence hoy en plataforma", p1);
        Acreditar(c, DeTrabajador(venceHoy, pss), canalP1, a => a.MarcarAceptada(VigenciaEnPlataforma.VenceEl(_hoy)));

        var rechazadaEnP1 = Alta(c, empresaE1, "w rechazada en P1 y trabaja tambien en P2", p1, p2);
        Acreditar(c, DeTrabajador(rechazadaEnP1, pss), canalP1, Rechazada);

        var aceptada = Alta(c, empresaE1, "w aceptada y vigente en plataforma", p1);
        Acreditar(c, DeTrabajador(aceptada, pss), canalP1, a => a.MarcarAceptada(VigenciaEnPlataforma.VenceEl(_hoy.AddDays(100))));

        var sinConfirmar = Alta(c, empresaE1, "w aceptada sin confirmar", p1);
        Acreditar(c, DeTrabajador(sinConfirmar, pss), canalP1, a => a.MarcarAceptada(VigenciaEnPlataforma.SinConfirmar));

        var pendiente = Alta(c, empresaE1, "w pendiente de subir", p1);
        Acreditar(c, DeTrabajador(pendiente, pss), canalP1, _ => { });

        var subida = Alta(c, empresaE1, "w subida esperando respuesta", p1);
        Acreditar(c, DeTrabajador(subida, pss), canalP1, a => a.MarcarSubida());

        var deBaja = Alta(c, empresaE1, "w rechazada pero de baja en P1", p1);
        Acreditar(c, DeTrabajador(deBaja, pss), canalP1, Rechazada);
        var asignacionDeBaja = c.Asignaciones.Single(a => a.TrabajadorId == deBaja);
        asignacionDeBaja.DarDeBaja(_hoy.AddDays(-1));
        c.SaveChanges();

        var tipoOpcional = Alta(c, empresaE1, "w rechazada de un tipo que no aplica", p1);
        Acreditar(c, DeTrabajador(tipoOpcional, opcional), canalP1, Rechazada);

        // --- Sujeto Empresa, canal de P1: bloquea a todos los Trabajadores de esa Empresa asignados a P1 ---------------------
        var certificadoE2 = DeEmpresa(empresaE2, certificado);
        Acreditar(c, certificadoE2, canalP1, Rechazada);
        Alta(c, empresaE2, "e2 uno en P1", p1);
        Alta(c, empresaE2, "e2 dos en P1", p1);
        Alta(c, empresaE2, "e2 solo en P2", p2);

        // --- Sujeto Empresa sin ningún Trabajador suyo en el Centro de la plataforma (P3) ----------------------------------
        var certificadoE3 = DeEmpresa(empresaE3, certificado);
        Acreditar(c, certificadoE3, canalP3, a => a.MarcarAceptada(VigenciaEnPlataforma.VenceEl(_hoy.AddDays(-3))));
        Alta(c, empresaE1, "w de otra Empresa en P3", p3);

        // --- P5 y P6: sin ningun otro problema, para ver que hace el veredicto de la plataforma EN SOLITARIO con el Centro ---
        var solaVencida = Alta(c, empresaE1, "w5 vigente en TALVEG y vencida en plataforma", p5);
        Acreditar(c, DeTrabajador(solaVencida, pss), canalP5, a => a.MarcarAceptada(VigenciaEnPlataforma.VenceEl(_hoy.AddDays(-2))));
        var solaRechazada = Alta(c, empresaE1, "w6 vigente en TALVEG y rechazada en plataforma", p6);
        Acreditar(c, DeTrabajador(solaRechazada, pss), canalP6, Rechazada);
        Alta(c, empresaE1, "w6 colega sin problema", p6);
        DeTrabajador(c.Trabajadores.Single(t => t.Apellidos == "w6 colega sin problema").Id, pss);

        // --- P4: el PSS es bloqueante documental ----------------------------------------------------------------------------
        // Cumple el documento, pero la plataforma lo rechazó: bloquea solo por la plataforma.
        var cumpleYRechazada = Alta(c, empresaE1, "w4 cumple el PSS y la plataforma lo rechaza", p4);
        Acreditar(c, DeTrabajador(cumpleYRechazada, pss), canalP4, Rechazada);

        // El PSS ya vencio en TALVEG (bloqueo documental) Y ademas esta rechazado: dos causas, dos filas (Vencido y
        // RechazadoPorPlataforma), un solo Trabajador bloqueado.
        var vencidoYRechazada = Alta(c, empresaE1, "w4 PSS vencido y rechazado", p4);
        var vencido = Documento.DeTrabajador(vencidoYRechazada, pss.Id, emision, VigenciaDocumento.VenceEl(_hoy.AddDays(-30)));
        c.Documentos.Add(vencido);
        c.SaveChanges();
        Acreditar(c, vencido, canalP4, Rechazada);

        // Sin PSS en absoluto: bloqueo documental Ausente, sin plataforma.
        Alta(c, empresaE1, "w4 sin documento", p4);

        // El mismo Trabajador, bien en TALVEG y aceptado en plataforma con vigencia futura: no bloquea.
        var limpio = Alta(c, empresaE1, "w4 limpio", p4);
        Acreditar(c, DeTrabajador(limpio, pss), canalP4, a => a.MarcarAceptada(VigenciaEnPlataforma.VenceEl(_hoy.AddDays(100))));
    }

    private async Task<IMediator> MediadorDeLaColaAsync()
    {
        if (_servicios is not null)
            return _servicios.GetRequiredService<IMediator>();

        var tenantActual = new TenantActualAmbiental { TenantId = Tenant };
        _contextoDeLaCola = CrearContexto();
        var contexto = _contextoDeLaCola;
        var servicios = new ServiceCollection();
        servicios.AddApplication();
        servicios.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        servicios.AddSingleton<ITenantActual>(tenantActual);
        servicios.AddSingleton<IUnitOfWork>(contexto);
        servicios.AddSingleton<CaeManager.Application.Tenants.ITenantsQueryContext>(contexto);
        servicios.AddSingleton<CaeManager.Application.Empresas.IEmpresasQueryContext>(contexto);
        servicios.AddSingleton<CaeManager.Application.Centros.ICentrosQueryContext>(contexto);
        servicios.AddSingleton<CaeManager.Application.Trabajadores.ITrabajadoresQueryContext>(contexto);
        servicios.AddSingleton<CaeManager.Application.TiposDocumento.ITiposDocumentoQueryContext>(contexto);
        servicios.AddSingleton<CaeManager.Application.Documentos.IDocumentosQueryContext>(contexto);
        servicios.AddSingleton<CaeManager.Application.DocumentosIa.IDocumentosIaQueryContext>(contexto);
        servicios.AddSingleton<CaeManager.Application.Asignaciones.IAsignacionesQueryContext>(contexto);
        servicios.AddSingleton<CaeManager.Application.Configuracion.IConfiguracionQueryContext>(contexto);
        servicios.AddSingleton<CaeManager.Application.Visitas.IVisitasQueryContext>(contexto);
        servicios.AddSingleton<CaeManager.Application.Comunicaciones.IComunicacionesQueryContext>(contexto);
        servicios.AddSingleton<CaeManager.Application.Integraciones.IProveedoresPlataformaCaeQueryContext>(contexto);
        servicios.AddSingleton<IAlcanceDatosService>(new AlcanceDatosServiceFalso());
        servicios.AddSingleton<ICurrentUserService>(new CurrentUserServiceFalso(Guid.NewGuid(), tenantOrigenId: Tenant));
        _servicios = servicios.BuildServiceProvider();
        await Task.CompletedTask;
        return _servicios.GetRequiredService<IMediator>();
    }

    /// <summary>La foto de todas las superficies de la plataforma: sección → líneas ordenadas.</summary>
    public async Task<SortedDictionary<string, List<string>>> TomarFotoAsync()
    {
        await using var c = CrearContexto();
        var alcance = new AlcanceDatosServiceFalso();
        var foto = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        List<string> Seccion(string nombre) => foto.TryGetValue(nombre, out var existente) ? existente : foto[nombre] = [];

        var centros = await c.Centros.ToDictionaryAsync(x => x.Id, x => x.Nombre);
        var trabajadores = await c.Trabajadores.ToDictionaryAsync(x => x.Id, x => x.Apellidos);
        var empresas = await c.Empresas.ToDictionaryAsync(x => x.Id, x => x.RazonSocial);
        var tipos = await c.TiposDocumento.ToDictionaryAsync(x => x.Id, x => x.Nombre);
        string Cen(Guid id) => centros.GetValueOrDefault(id, id.ToString());
        string Tra(Guid id) => trabajadores.GetValueOrDefault(id, id.ToString());
        string Emp(Guid id) => empresas.GetValueOrDefault(id, id.ToString());
        string Tip(Guid id) => tipos.GetValueOrDefault(id, id.ToString());
        var centroIds = centros.Keys.ToList();

        var calculoCentro = new CalculoEstadoCentroService(c, c, c, c, c, c);

        // 1. Centro: estado, % y recuentos (lista de Centros).
        var lista = await new ObtenerCentrosQueryHandler(c, c, alcance, calculoCentro).Handle(new ObtenerCentrosQuery(null, null), CancellationToken.None);
        Seccion("Centro: estado y recuentos").AddRange(lista.Elementos.Select(e =>
            $"{e.Nombre} | estado={e.Estado} | % lista={e.CumplimientoPorcentaje?.ToString() ?? "-"} | vencidas={e.Recuentos.TotalVencidas} | proximas={e.Recuentos.TotalProximas}"));

        // 2. Centro: causas del estado (el desglose del panel «Por qué no está Vigente»).
        var causas = await calculoCentro.CalcularAsync(centroIds, CancellationToken.None);
        foreach (var (centroId, resultado) in causas)
            foreach (var causa in resultado.Causas)
                Seccion("Centro: causas").Add($"{Cen(centroId)} | {causa.Descripcion} | estado={causa.Estado?.ToString() ?? "-"} | {causa.Ambito}");

        // 3. Mi trabajo (filas de requisito pendiente): lo que lee la cola de la Bandeja.
        var evaluacion = new EvaluacionDeAccesoPorCentroService(c, c, c, c, c, alcance);
        var handlerBloqueantes = new ObtenerDocumentacionBloqueantePendienteQueryHandler(c, c, c, evaluacion);
        var miTrabajo = await handlerBloqueantes.Handle(new ObtenerDocumentacionBloqueantePendienteQuery(), CancellationToken.None);
        Seccion("Mi trabajo: requisitos").AddRange(miTrabajo.Select(f =>
            $"{Cen(f.CentroId)} | {Tra(f.TrabajadorId)} | {Tip(f.TipoDocumentoId)} | {f.Ambito} | {f.Situacion}"));

        // 4. Centro 360 y panel del Centro: Trabajadores bloqueados en ESE Centro.
        foreach (var centroId in centroIds)
        {
            var filas = await handlerBloqueantes.Handle(
                new ObtenerDocumentacionBloqueantePendienteQuery(centroId, IncluirBloqueosDePlataforma: true), CancellationToken.None);
            Seccion("Centro 360: Trabajadores bloqueados").AddRange(filas.Select(f =>
                $"{Cen(f.CentroId)} | {Tra(f.TrabajadorId)} | {Tip(f.TipoDocumentoId)} | {f.Ambito} | {f.Situacion}"));
        }

        // 5. Tarjeta de Inicio, Visión de cartera (la organización dentro de la cartera) y ficha del Cliente empresarial.
        var kpis = await new ObtenerKpisDashboardQueryHandler(c, c, c, c, c, alcance, evaluacion)
            .Handle(new ObtenerKpisDashboardQuery(), CancellationToken.None);
        Seccion("Inicio").Add(
            $"centros={kpis.Centros} trabajadoresBloqueados={kpis.TrabajadoresBloqueados} " +
            $"centrosBloqueados={lista.Elementos.Count(e => e.Estado == EstadoCentro.Bloqueado)} tasa={kpis.TasaCumplimientoDocumental}");

        var cartera = ObtenerKpisGlobalesQueryHandler.Fusionar(
            [(new ClienteAutorizadoDto(Tenant, "Organizacion de la foto", false), kpis)]);
        var organizacion = cartera.ClientesConMasRiesgo.Single();
        Seccion("Vision de cartera").Add(
            $"{organizacion.Nombre} | trabajadoresBloqueados={organizacion.TrabajadoresBloqueados} | tieneBloqueos={organizacion.TieneBloqueos} | " +
            $"admiteVeredictoVerde={organizacion.AdmiteVeredictoVerde} | trabajadoresBloqueadosGlobal={cartera.TrabajadoresBloqueados}");

        foreach (var cliente in lista.Elementos.GroupBy(e => e.ClienteId))
        {
            var bloqueosDelCliente = new List<DocumentacionBloqueantePendienteDto>();
            foreach (var centro in cliente)
                bloqueosDelCliente.AddRange(await handlerBloqueantes.Handle(
                    new ObtenerDocumentacionBloqueantePendienteQuery(centro.Id, IncluirBloqueosDePlataforma: true), CancellationToken.None));
            Seccion("Cliente empresarial: ficha").Add(
                $"{Emp(cliente.Key)} | trabajadoresBloqueados={bloqueosDelCliente.Select(b => b.TrabajadorId).Distinct().Count()} | " +
                $"centrosBloqueados={cliente.Count(e => e.Estado == EstadoCentro.Bloqueado)} | centrosConVencidos={cliente.Count(e => e.Recuentos.TotalVencidas > 0)}");
        }

        // 6. La cola de la Bandeja (la que leen Inicio y /bandeja): grupos y la marca «bloquea acceso» de cada item.
        var mediador = await MediadorDeLaColaAsync();
        var bandeja = await mediador.Send(new ObtenerBandejaAgrupadaQuery());
        foreach (var grupo in bandeja.Grupos)
        {
            Seccion("Cola: grupos").Add($"{grupo.Titulo} | bloqueaAcceso={grupo.BloqueaAcceso} | items={grupo.Items.Count}");
            foreach (var item in grupo.Items.Where(i => i.Tipo is TipoItemBandeja.PlataformaRechazada or TipoItemBandeja.PlataformaVencida or TipoItemBandeja.RequisitoPendiente))
                Seccion("Cola: items de plataforma y requisitos").Add(
                    $"{(item.CentroId is { } ci ? Cen(ci) : "sin centro")} | {item.Tipo} | bloqueaElAcceso={ObtenerBandejaAgrupadaQueryHandler.BloqueaElAcceso(item)} | {item.Titulo}");
        }

        foreach (var lineas in foto.Values)
            lineas.Sort(StringComparer.Ordinal);
        return foto;
    }

    public async ValueTask DisposeAsync()
    {
        _servicios?.Dispose();
        if (_contextoDeLaCola is not null)
            await _contextoDeLaCola.DisposeAsync();
    }
}
