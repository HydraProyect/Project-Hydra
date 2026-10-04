using CaeManager.Application.Alertas;
using CaeManager.Application.Alertas.Queries.ObtenerAlertas;
using CaeManager.Application.Asignaciones;
using CaeManager.Application.Asignaciones.Queries.ObtenerAsignacionesDocumentacionPorCentro;
using CaeManager.Application.Centros;
using CaeManager.Application.Centros.Queries.ObtenerCentros;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas.Queries.ObtenerCumplimientoEmpresa;
using CaeManager.Application.Subcontratas;
using CaeManager.Application.Trabajadores.Queries.ObtenerDocumentacionPorCentroDeTrabajador;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Subcontratas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.IntegrationTests.Coherencia;

/// <summary>
/// La «foto» de las superficies que consumen documentación, sobre un conjunto de datos representativo con dos Tenants:
/// Centros con y sin tolerancia, documentos vigentes, próximos, vencidos (también «vencidos dentro de la tolerancia»), sin
/// confirmar, «No caduca» y faltantes, de Empresa y de Trabajador, y altas nuevas sin documentación. Cada sección de la
/// foto es una lista de líneas ordenada y escrita con NOMBRES (no Guids), para compararla entre dos estados de los mismos
/// datos —o entre dos árboles de código— y explicar cada diferencia por una regla decidida.
///
/// <para>
/// Secciones: filas de Mi trabajo (requisitos bloqueantes pendientes), Trabajadores bloqueados por Centro, alertas, estado
/// y % de cada Centro (servicio, lista y acordeón), % del Trabajador, % de la Empresa, % del Cliente empresarial, % de la
/// Subcontrata, y los números de la tarjeta de Inicio. Lo específico del bloqueo (Mi trabajo y bloqueados) y la siembra de la
/// tolerancia viven en <see cref="FotoDeAcceso"/>, que es lo único que cambia entre el código anterior y el de la tolerancia.
/// </para>
/// </summary>
internal sealed class EscenarioDeFotoDeSuperficies
{
    internal const int UmbralAmbarDias = 30;
    internal const int UmbralRojoDias = 15;
    private const string LetrasDni = "TRWAGMYFPDXBNJZSQVHLCKE";

    private readonly string _cadenaConexion;
    private readonly DateOnly _hoy = DiaDeNegocio.Hoy();
    private int _dni = 10_000_000;

    public EscenarioDeFotoDeSuperficies(string cadenaConexion) => _cadenaConexion = cadenaConexion;

    public Guid TenantUno { get; } = Guid.NewGuid();
    public Guid TenantDos { get; } = Guid.NewGuid();

    public CaeManagerDbContext CrearContexto(Guid tenant)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;
        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private string NuevoDni() => $"{++_dni:D8}{LetrasDni[_dni % 23]}";

    private static void Umbrales(CaeManagerDbContext c)
    {
        var parametros = c.ParametrosSistema.SingleOrDefault();
        if (parametros is null)
            c.ParametrosSistema.Add(new ParametroSistema(UmbralAmbarDias, UmbralRojoDias));
        else
            parametros.Actualizar(UmbralAmbarDias, UmbralRojoDias);
        c.SaveChanges();
    }

    private Guid Alta(CaeManagerDbContext c, Empresa? empresa, Empresa? subcontrata, string etiqueta, params Centro[] centros)
    {
        var t = empresa is not null
            ? Trabajador.DeEmpresa(empresa.Id, "Caso", etiqueta, NuevoDni())
            : Trabajador.DeSubcontrata(subcontrata!.Id, "Caso", etiqueta, NuevoDni());
        c.Trabajadores.Add(t);
        c.SaveChanges();
        foreach (var centro in centros)
            c.Asignaciones.Add(new Asignacion(t.Id, centro.Id, _hoy.AddDays(-400)));
        c.SaveChanges();
        return t.Id;
    }

    private void DocumentoDeTrabajador(CaeManagerDbContext c, Guid trabajadorId, TipoDocumento tipo, VigenciaDocumento vigencia)
    {
        c.Documentos.Add(Documento.DeTrabajador(trabajadorId, tipo.Id, _hoy.AddDays(-400), vigencia));
        c.SaveChanges();
    }

    private void DocumentoDeEmpresa(CaeManagerDbContext c, Empresa empresa, TipoDocumento tipo, VigenciaDocumento vigencia)
    {
        c.Documentos.Add(Documento.DeEmpresa(empresa.Id, tipo.Id, _hoy.AddDays(-400), vigencia));
        c.SaveChanges();
    }

    /// <summary>
    /// Siembra los dos Tenants. Tenant uno: Clientes empresariales X (Centros A y B) e Y (Centros C y D); el certificado
    /// de Empresa es requisito bloqueante en A, B y C pero NO en D; el PSS lo es en los cuatro. Tenant dos: un Cliente
    /// empresarial con un Centro y dos Trabajadores. Después llama a <see cref="FotoDeAcceso.SembrarTolerancias"/>.
    /// </summary>
    public async Task SembrarAsync()
    {
        await using (var migracion = CrearContexto(TenantUno))
            await migracion.Database.MigrateAsync();

        await using (var c = CrearContexto(TenantUno))
        {
            Umbrales(c);
            var clienteX = Empresa.CrearComoCliente("Cliente X", "B12345674", false, null, null);
            var clienteY = Empresa.CrearComoCliente("Cliente Y", "B87654323", false, null, null);
            var empresaP = new Empresa("Empresa P");
            var empresaQ = new Empresa("Empresa Q");
            var empresaR = new Empresa("Empresa R");
            var subcontrataS = Empresa.CrearComoSubcontrata("Subcontrata S", null, NivelServicioSubcontrata.Gestionada.ToString());
            c.Empresas.AddRange(clienteX, clienteY, empresaP, empresaQ, empresaR, subcontrataS);
            c.SaveChanges();

            var centroA = new Centro(clienteX.Id, empresaP.Id, "Centro A");
            var centroB = new Centro(clienteX.Id, empresaP.Id, "Centro B");
            var centroC = new Centro(clienteY.Id, empresaP.Id, "Centro C");
            var centroD = new Centro(clienteY.Id, empresaP.Id, "Centro D");
            c.Centros.AddRange(centroA, centroB, centroC, centroD);

            var pss = new TipoDocumento("PSS firmado", null, aplicaVencimientoAutomatico: false, 1, AmbitoAplicacion.Trabajador, requerido: RequisitoDocumental.Si);
            var certificado = new TipoDocumento("Certificado de la Seguridad Social", null, aplicaVencimientoAutomatico: false, 2, AmbitoAplicacion.Empresa);
            c.TiposDocumento.AddRange(pss, certificado);
            c.SaveChanges();

            foreach (var centro in new[] { centroA, centroB, centroC, centroD })
                c.TiposDocumentoCentros.Add(new TipoDocumentoCentro(pss.Id, centro.Id, incluido: true, bloqueaAcceso: true));
            foreach (var centro in new[] { centroA, centroB, centroC })
                c.TiposDocumentoCentros.Add(new TipoDocumentoCentro(certificado.Id, centro.Id, incluido: true, bloqueaAcceso: true));
            c.SaveChanges();

            // Empresa P: el certificado al día (no bloquea a los suyos); un Trabajador por situación del PSS, todos en el Centro A.
            DocumentoDeEmpresa(c, empresaP, certificado, VigenciaDocumento.NoCaduca);
            void DeP(string etiqueta, VigenciaDocumento? vigencia, params Centro[] centros)
            {
                var id = Alta(c, empresaP, null, etiqueta, centros);
                if (vigencia is { } v) DocumentoDeTrabajador(c, id, pss, v);
            }

            DeP("p vencido ayer", VigenciaDocumento.VenceEl(_hoy.AddDays(-1)), centroA);
            DeP("p vencido hace 10", VigenciaDocumento.VenceEl(_hoy.AddDays(-10)), centroA);
            DeP("p vencido hace 15", VigenciaDocumento.VenceEl(_hoy.AddDays(-15)), centroA);
            DeP("p vencido hace 16", VigenciaDocumento.VenceEl(_hoy.AddDays(-16)), centroA);
            DeP("p vencido hace 20", VigenciaDocumento.VenceEl(_hoy.AddDays(-20)), centroA);
            DeP("p vence hoy", VigenciaDocumento.VenceEl(_hoy), centroA);
            DeP("p proximo", VigenciaDocumento.VenceEl(_hoy.AddDays(20)), centroA);
            DeP("p vigente en A y B", VigenciaDocumento.VenceEl(_hoy.AddDays(200)), centroA, centroB);
            DeP("p no caduca", VigenciaDocumento.NoCaduca, centroA);
            DeP("p sin confirmar", VigenciaDocumento.SinConfirmar, centroA);
            DeP("p alta nueva sin documentos", null, centroA);

            // Empresa Q: no tiene el certificado (ausente). q1 tiene el PSS vencido ayer en A; q2 trabaja en C con el PSS vigente.
            var q1 = Alta(c, empresaQ, null, "q vencido ayer en A", centroA);
            DocumentoDeTrabajador(c, q1, pss, VigenciaDocumento.VenceEl(_hoy.AddDays(-1)));
            var q2 = Alta(c, empresaQ, null, "q vigente en C", centroC);
            DocumentoDeTrabajador(c, q2, pss, VigenciaDocumento.VenceEl(_hoy.AddDays(200)));

            // Empresa R: certificado vencido hace 10 días. r1 (A y B) y r2 (solo D, que no exige el certificado) con el PSS vigente.
            DocumentoDeEmpresa(c, empresaR, certificado, VigenciaDocumento.VenceEl(_hoy.AddDays(-10)));
            var r1 = Alta(c, empresaR, null, "r1 en A y B", centroA, centroB);
            DocumentoDeTrabajador(c, r1, pss, VigenciaDocumento.VenceEl(_hoy.AddDays(200)));
            var r2 = Alta(c, empresaR, null, "r2 solo en D", centroD);
            DocumentoDeTrabajador(c, r2, pss, VigenciaDocumento.VenceEl(_hoy.AddDays(200)));

            // Subcontrata S (con el certificado vigente): s1 en B y C vigente, s2 sin confirmar, s3 proximo, s4 sin documento.
            DocumentoDeEmpresa(c, subcontrataS, certificado, VigenciaDocumento.VenceEl(_hoy.AddDays(200)));
            var s1 = Alta(c, null, subcontrataS, "s1 vigente en B y C", centroB, centroC);
            DocumentoDeTrabajador(c, s1, pss, VigenciaDocumento.VenceEl(_hoy.AddDays(130)));
            var s2 = Alta(c, null, subcontrataS, "s2 sin confirmar", centroC);
            DocumentoDeTrabajador(c, s2, pss, VigenciaDocumento.SinConfirmar);
            var s3 = Alta(c, null, subcontrataS, "s3 proximo", centroC);
            DocumentoDeTrabajador(c, s3, pss, VigenciaDocumento.VenceEl(_hoy.AddDays(25)));
            Alta(c, null, subcontrataS, "s4 sin documento", centroC);
        }

        await using (var c = CrearContexto(TenantDos))
        {
            Umbrales(c);
            var cliente = Empresa.CrearComoCliente("Cliente X2", "B12345674", false, null, null);
            var empresa = new Empresa("Empresa P2");
            c.Empresas.AddRange(cliente, empresa);
            c.SaveChanges();
            var centro = new Centro(cliente.Id, empresa.Id, "Centro A2");
            c.Centros.Add(centro);
            var pss = new TipoDocumento("PSS firmado", null, aplicaVencimientoAutomatico: false, 1, AmbitoAplicacion.Trabajador, requerido: RequisitoDocumental.Si);
            c.TiposDocumento.Add(pss);
            c.SaveChanges();
            c.TiposDocumentoCentros.Add(new TipoDocumentoCentro(pss.Id, centro.Id, incluido: true, bloqueaAcceso: true));
            c.SaveChanges();
            var t1 = Alta(c, empresa, null, "t2 vencido hace 50", centro);
            DocumentoDeTrabajador(c, t1, pss, VigenciaDocumento.VenceEl(_hoy.AddDays(-50)));
            Alta(c, empresa, null, "t2 alta nueva sin documentos", centro);
        }

        await FotoDeAcceso.SembrarTolerancias(this);
    }

    /// <summary>La foto de todas las superficies de un Tenant: sección → líneas ordenadas.</summary>
    public async Task<SortedDictionary<string, List<string>>> TomarFotoAsync(Guid tenant)
    {
        await using var c = CrearContexto(tenant);
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
        string Fr(FraccionCumplimiento f) => $"{f.AlDia}/{f.Requeridos}";

        var calculoCentro = new CalculoEstadoCentroService(c, c, c, c, c, c);
        var calculoDocumental = new CalculoEstadoDocumentalService(c, c);
        var calculoSubcontrata = new CalculoEstadoSubcontrataService(c, c, c, c, c, c, alcance);
        var centroIds = centros.Keys.ToList();

        // 1 y 2. Bloqueo: Mi trabajo y Trabajadores bloqueados por Centro (lo único que depende de la tolerancia).
        foreach (var (seccion, lineas) in await FotoDeAcceso.TomarAsync(c, alcance, Cen, Tra, Emp, Tip))
            foto[seccion] = lineas;

        // 3. Alertas.
        var alertas = await new ObtenerAlertasQueryHandler(
                c, c, c, c, c, c, c, new ResolverClientePrincipalService(c, c, c), alcance, new DocumentosFaltantesService(c, c, c))
            .Handle(new ObtenerAlertasQuery(), CancellationToken.None);
        Seccion("Alertas").AddRange(alertas.Select(a =>
            $"{Tra(a.TrabajadorId)} | {Tip(a.TipoDocumentoId)} | {a.Estado} | {(a.CentroId is { } ci ? Cen(ci) : "sin centro")} | {a.FechaVencimiento:yyyy-MM-dd}".Replace(_hoy.ToString("yyyy-MM-dd"), "HOY")));

        // 4. Contexto Centro: estado y % (servicio, lista y acordeón).
        var porCentro = await calculoCentro.CalcularCumplimientoAsync(centroIds, CancellationToken.None);
        var lista = await new ObtenerCentrosQueryHandler(c, c, alcance, calculoCentro).Handle(new ObtenerCentrosQuery(null, null), CancellationToken.None);
        Seccion("Centro: estado y %").AddRange(lista.Elementos.Select(e =>
            $"{e.Nombre} | estado={e.Estado} | % lista={e.CumplimientoPorcentaje?.ToString() ?? "-"} | servicio={Fr(porCentro[e.Id])}"));
        var acordeon = new ObtenerAsignacionesDocumentacionPorCentroQueryHandler(c, c, c, c, c, c, alcance);
        foreach (var centro in centroIds)
        {
            var filas = await acordeon.Handle(new ObtenerAsignacionesDocumentacionPorCentroQuery(centro), CancellationToken.None);
            Seccion("Centro: acordeon").Add($"{Cen(centro)} | suma de Trabajadores={Fr(FraccionCumplimiento.Sumar(filas.Select(t => t.Cumplimiento)))}");
        }

        // 5. Contexto Trabajador.
        var documentacion = new ObtenerDocumentacionPorCentroDeTrabajadorQueryHandler(c, c, c, c, c, c, alcance);
        foreach (var trabajador in trabajadores.Keys)
        {
            var filas = await documentacion.Handle(new ObtenerDocumentacionPorCentroDeTrabajadorQuery(trabajador), CancellationToken.None);
            Seccion("Trabajador").Add($"{Tra(trabajador)} | {Fr(FraccionCumplimiento.Sumar(filas.Select(x => x.Cumplimiento)))}");
        }

        // 6. Contexto Empresa (panel y lista).
        var cumplimientoEmpresa = new ObtenerCumplimientoEmpresaQueryHandler(c, c, calculoCentro, alcance);
        foreach (var empresa in empresas.Keys)
            Seccion("Empresa").Add($"{Emp(empresa)} | panel={(await cumplimientoEmpresa.Handle(new ObtenerCumplimientoEmpresaQuery(empresa), CancellationToken.None))?.ToString() ?? "-"}");

        // 7. Contexto Cliente empresarial (la función es el contrato; hoy sin pantalla).
        var pares = await calculoCentro.ObtenerParesExigidosAsync(centroIds, CancellationToken.None);
        foreach (var cliente in await c.Centros.Select(x => x.ClienteId).Distinct().ToListAsync())
            Seccion("Cliente empresarial").Add($"{Emp(cliente)} | {Fr(CumplimientoDocumental.De(ContextoCumplimiento.ClienteEmpresarial, cliente, pares))}");
        Seccion("Pares exigidos").Add(pares.Count.ToString());

        // 8. Subcontrata.
        foreach (var empresa in await c.Empresas.Where(e => e.NivelServicio != null).Select(e => e.Id).ToListAsync())
            Seccion("Subcontrata").Add($"{Emp(empresa)} | {Fr((await calculoSubcontrata.CalcularCumplimientoAsync([empresa], CancellationToken.None))[empresa])}");

        // 9. Tarjeta de Inicio.
        var kpis = await new ObtenerKpisDashboardQueryHandler(c, c, c, c, c, alcance, new EvaluacionDeAccesoPorCentroService(c, c, c, c, c, alcance)).Handle(new ObtenerKpisDashboardQuery(), CancellationToken.None);
        Seccion("Inicio").Add(
            $"activos={kpis.TrabajadoresActivos} centros={kpis.Centros} vencidos={kpis.DocumentosVencidos} urgentes={kpis.DocumentosUrgentes} " +
            $"proximos={kpis.DocumentosProximos} vigentes={kpis.DocumentosVigentes} tasa={kpis.TasaCumplimientoDocumental} fraccion={Fr(kpis.Fraccion)} " +
            $"sinConfirmar={kpis.DocumentosSinConfirmar} sinCaducidad={kpis.DocumentosSinCaducidad} sinDatos={kpis.SinDatos}");

        foreach (var lineas in foto.Values)
            lineas.Sort(StringComparer.Ordinal);
        return foto;
    }
}
