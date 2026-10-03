using CaeManager.Application.Bandeja.Queries.ObtenerBandejaAgrupada;
using CaeManager.Application.Bandeja.Queries.ObtenerBandejaGestor;
using CaeManager.Application.Centros.Queries.ObtenerDocumentacionBloqueantePendiente;
using CaeManager.Application.Common;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Coherencia;

/// <summary>
/// Bloqueo de acceso por documento bloqueante, dirigido por tabla y contra PostgreSQL con RLS real (el rol
/// <c>cae_app_runtime</c> de producción). Decisión del propietario del producto, 2026-10-03:
/// <list type="number">
/// <item><b>R1</b> — un documento bloqueante de Trabajador ausente o vencido bloquea a ese Trabajador; Próximo,
/// Urgente (válidos hoy), «No caduca» y «Sin confirmar» no.</item>
/// <item><b>R2</b> — un documento bloqueante de Empresa ausente o vencido bloquea a TODOS los Trabajadores de esa
/// Empresa en TODOS los Centros del Tenant propietario (también a los de un subcontratista), aunque su
/// documentación personal esté completa; nunca cruza de un Tenant a otro.</item>
/// <item><b>R3</b> — el sujeto es el Trabajador o la Empresa; el Centro no es el sujeto.</item>
/// </list>
///
/// <para>
/// Las superficies que consumen la regla (la consulta de Mi trabajo y la fusión de la cola que la pinta) tienen que
/// dar la misma respuesta que la tabla. <b>Lo que esta tabla NO fija, a propósito</b>: qué enseña el semáforo del
/// Centro (<c>CalculoEstadoCentroService</c>) con Trabajadores o Empresas bloqueados. Hoy solo lo pone en Bloqueado la
/// ausencia total de un tipo de Trabajador (no el vencido, no el de Empresa) y la regla R3 deja ese estado sin
/// definir: es una decisión de producto pendiente, y una fila aquí la consagraría.
/// </para>
/// </summary>
public class CoherenciaDelBloqueoDeAccesoEntreSuperficiesTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();
    private readonly Guid _tenantC = Guid.NewGuid();
    private readonly Guid _tenantD = Guid.NewGuid();
    private readonly DateOnly _hoy = DiaDeNegocio.Hoy();
    private readonly Guid _usuario = Guid.NewGuid();
    private static int _contadorDni = 10_000_000;

    private CaeManagerDbContext _propietario = null!;
    private readonly List<CaeManagerDbContext> _contextosRuntime = [];

    private Guid _centroA1, _centroA2, _centroA3SinGestion;
    private Guid _tipoPss, _tipoCertificado;
    private readonly List<Caso> _casos = [];

    private sealed record Esperado(AmbitoAplicacion Ambito, SituacionDeRequisitoBloqueante Situacion, bool EsAltaNueva);

    /// <summary>Una fila de la tabla: un Trabajador, dónde está asignado y a qué Centros y por qué debe estar bloqueado.</summary>
    private sealed class Caso(string nombre, string superficie)
    {
        public string Nombre { get; } = nombre;
        public string Superficie { get; } = superficie;
        public Guid TrabajadorId { get; set; }

        /// <summary>Centro → lo esperado en él; <c>null</c> = no debe aparecer.</summary>
        public Dictionary<Guid, Esperado?> PorCentro { get; } = [];
    }

    public async Task InitializeAsync()
    {
        var tenantPorAmbito = new TenantActualPorAmbito();
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantPorAmbito))
            .Options;
        _propietario = new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenantPorAmbito);
        await _propietario.Database.MigrateAsync();

        await SembrarTenantAAsync();
        await SembrarTenantBAsync();
        await SembrarTenantCAsync();
        await SembrarTenantDAsync();
    }

    public async Task DisposeAsync()
    {
        foreach (var contexto in _contextosRuntime)
            await contexto.DisposeAsync();
        await _propietario.DisposeAsync();
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
    }

    // ---------- Siembra ----------

    private static string Dni()
    {
        var numero = Interlocked.Increment(ref _contadorDni);
        return $"{numero:D8}{"TRWAGMYFPDXBNJZSQVHLCKE"[numero % 23]}";
    }

    private async Task SembrarTenantAAsync()
    {
        using var ambito = AmbitoTenantExplicito.Establecer(_tenantA);
        var c = _propietario;

        var cliente = Empresa.CrearComoCliente("Cliente empresarial A", "B12345674", false, null, null);
        var contratista = new Empresa("Contratista A S.L.");
        var empresaBase = new Empresa("Empresa base A");
        var empAusente = new Empresa("Empresa sin certificado");
        var empVencida = new Empresa("Empresa con certificado vencido");
        var empVenceHoy = new Empresa("Empresa con certificado que vence hoy");
        var empNoCaduca = new Empresa("Empresa con certificado que no caduca");
        var empSinConfirmar = new Empresa("Empresa con certificado sin confirmar");
        var empVigente = new Empresa("Empresa con certificado vigente");
        var subAusente = new Empresa("Subcontratista sin certificado");
        var subVigente = new Empresa("Subcontratista con certificado");
        c.Empresas.AddRange(cliente, contratista, empresaBase, empAusente, empVencida, empVenceHoy, empNoCaduca,
            empSinConfirmar, empVigente, subAusente, subVigente);
        await c.SaveChangesAsync();

        var centro1 = new Centro(cliente.Id, contratista.Id, "Centro A1");
        var centro2 = new Centro(cliente.Id, contratista.Id, "Centro A2");
        var centro3 = new Centro(cliente.Id, contratista.Id, "Centro A3 sin gestion CAE");
        centro3.EstablecerGestionCae(ModalidadGestionCae.SinGestionCae);
        c.Centros.AddRange(centro1, centro2, centro3);

        var pss = new TipoDocumento("PSS firmado", null, false, 1, AmbitoAplicacion.Trabajador);
        var certificado = new TipoDocumento("Certificado de la Seguridad Social", null, false, 2, AmbitoAplicacion.Empresa);
        var deCliente = new TipoDocumento("Tipo de ambito Cliente", null, false, 3, AmbitoAplicacion.Cliente);
        c.TiposDocumento.AddRange(pss, certificado, deCliente);
        await c.SaveChangesAsync();

        // El PSS es bloqueante SOLO en el Centro 1; el certificado de Empresa, marcado en el Centro 1, bloquea en todos
        // (R2). El tipo de ambito Cliente tambien esta marcado: no tiene sujeto y no debe bloquear a nadie.
        c.TiposDocumentoCentros.AddRange(
            new TipoDocumentoCentro(pss.Id, centro1.Id, incluido: true, bloqueaAcceso: true),
            new TipoDocumentoCentro(certificado.Id, centro1.Id, incluido: true, bloqueaAcceso: true),
            new TipoDocumentoCentro(deCliente.Id, centro1.Id, incluido: true, bloqueaAcceso: true));
        await c.SaveChangesAsync();

        _centroA1 = centro1.Id;
        _centroA2 = centro2.Id;
        _centroA3SinGestion = centro3.Id;
        _tipoPss = pss.Id;
        _tipoCertificado = certificado.Id;

        var emision = _hoy.AddDays(-400);

        // La Empresa base y las de «vigente» cumplen el certificado: no bloquean a sus Trabajadores por R2.
        c.Documentos.Add(Documento.DeEmpresa(empresaBase.Id, certificado.Id, emision, VigenciaDocumento.NoCaduca));
        c.Documentos.Add(Documento.DeEmpresa(empVencida.Id, certificado.Id, emision, VigenciaDocumento.VenceEl(_hoy.AddDays(-1))));
        c.Documentos.Add(Documento.DeEmpresa(empVenceHoy.Id, certificado.Id, emision, VigenciaDocumento.VenceEl(_hoy)));
        c.Documentos.Add(Documento.DeEmpresa(empNoCaduca.Id, certificado.Id, emision, VigenciaDocumento.NoCaduca));
        c.Documentos.Add(Documento.DeEmpresa(empSinConfirmar.Id, certificado.Id, emision, VigenciaDocumento.SinConfirmar));
        c.Documentos.Add(Documento.DeEmpresa(empVigente.Id, certificado.Id, emision, VigenciaDocumento.VenceEl(_hoy.AddDays(200))));
        c.Documentos.Add(Documento.DeEmpresa(subVigente.Id, certificado.Id, emision, VigenciaDocumento.VenceEl(_hoy.AddDays(200))));
        await c.SaveChangesAsync();

        var trabajador = new Esperado(AmbitoAplicacion.Trabajador, SituacionDeRequisitoBloqueante.Ausente, true);
        var trabajadorVencido = new Esperado(AmbitoAplicacion.Trabajador, SituacionDeRequisitoBloqueante.Vencido, false);
        var empresaAusente = new Esperado(AmbitoAplicacion.Empresa, SituacionDeRequisitoBloqueante.Ausente, false);
        var empresaVencida = new Esperado(AmbitoAplicacion.Empresa, SituacionDeRequisitoBloqueante.Vencido, false);

        // R1: un Trabajador de la Empresa base, asignado solo al Centro 1, con el PSS en cada situacion.
        await AnadirR1Async(c, empresaBase.Id, "R1 · PSS ausente", null, trabajador);
        await AnadirR1Async(c, empresaBase.Id, "R1 · PSS vencido ayer", [VigenciaDocumento.VenceEl(_hoy.AddDays(-1))], trabajadorVencido);
        await AnadirR1Async(c, empresaBase.Id, "R1 · PSS vence hoy (Urgente, valido hoy)", [VigenciaDocumento.VenceEl(_hoy)], null);
        await AnadirR1Async(c, empresaBase.Id, "R1 · PSS Urgente a 5 dias", [VigenciaDocumento.VenceEl(_hoy.AddDays(5))], null);
        await AnadirR1Async(c, empresaBase.Id, "R1 · PSS Proximo a 20 dias", [VigenciaDocumento.VenceEl(_hoy.AddDays(20))], null);
        await AnadirR1Async(c, empresaBase.Id, "R1 · PSS vigente a 200 dias", [VigenciaDocumento.VenceEl(_hoy.AddDays(200))], null);
        await AnadirR1Async(c, empresaBase.Id, "R1 · PSS No caduca", [VigenciaDocumento.NoCaduca], null);
        await AnadirR1Async(c, empresaBase.Id, "R1 · PSS Sin confirmar", [VigenciaDocumento.SinConfirmar], null);
        await AnadirR1Async(c, empresaBase.Id, "R1 · PSS vencido y su renovacion vigente",
            [VigenciaDocumento.VenceEl(_hoy.AddDays(-30)), VigenciaDocumento.VenceEl(_hoy.AddDays(335))], null);

        // R2: un Trabajador con el PSS completo, asignado a los Centros 1, 2 y 3 (este sin gestion CAE), de cada Empresa.
        await AnadirR2Async(c, "R2 · Empresa sin el certificado", empAusente.Id, subcontrata: false, empresaAusente);
        await AnadirR2Async(c, "R2 · certificado de Empresa vencido ayer", empVencida.Id, subcontrata: false, empresaVencida);
        await AnadirR2Async(c, "R2 · certificado de Empresa que vence hoy", empVenceHoy.Id, subcontrata: false, null);
        await AnadirR2Async(c, "R2 · certificado de Empresa que no caduca", empNoCaduca.Id, subcontrata: false, null);
        await AnadirR2Async(c, "R2 · certificado de Empresa sin confirmar", empSinConfirmar.Id, subcontrata: false, null);
        await AnadirR2Async(c, "R2 · certificado de Empresa vigente", empVigente.Id, subcontrata: false, null);
        await AnadirR2Async(c, "R2 · Trabajador de un subcontratista sin el certificado", subAusente.Id, subcontrata: true, empresaAusente);
        await AnadirR2Async(c, "R2 · Trabajador de un subcontratista con el certificado", subVigente.Id, subcontrata: true, null);

        // Control: el requisito de Trabajador es de SU Centro. En el Centro 2 el PSS no es bloqueante y nadie tiene
        // que aparecer por no tenerlo.
        var sinPssEnOtroCentro = Trabajador.DeEmpresa(empresaBase.Id, "Control", "Centro dos", Dni());
        c.Trabajadores.Add(sinPssEnOtroCentro);
        await c.SaveChangesAsync();
        c.Asignaciones.Add(new Asignacion(sinPssEnOtroCentro.Id, centro2.Id, emision));
        await c.SaveChangesAsync();
        var control = new Caso("Control · sin PSS pero solo en el Centro 2, donde no es bloqueante", "Trabajador")
        {
            TrabajadorId = sinPssEnOtroCentro.Id
        };
        control.PorCentro[centro2.Id] = null;
        _casos.Add(control);

        // Control: una asignacion dada de baja no bloquea en ese Centro.
        var deBaja = Trabajador.DeEmpresa(empAusente.Id, "Control", "De baja", Dni());
        c.Trabajadores.Add(deBaja);
        await c.SaveChangesAsync();
        var asignacionBaja = new Asignacion(deBaja.Id, centro2.Id, emision);
        c.Asignaciones.Add(asignacionBaja);
        await c.SaveChangesAsync();
        asignacionBaja.DarDeBaja(_hoy.AddDays(-1));
        await c.SaveChangesAsync();
        var casoBaja = new Caso("Control · Trabajador de una Empresa sin certificado pero con la asignacion dada de baja", "Trabajador")
        {
            TrabajadorId = deBaja.Id
        };
        casoBaja.PorCentro[centro2.Id] = null;
        _casos.Add(casoBaja);
    }

    private async Task AnadirR1Async(CaeManagerDbContext c, Guid empresaId, string nombre, VigenciaDocumento[]? vigencias, Esperado? esperado)
    {
        var trabajador = Trabajador.DeEmpresa(empresaId, "Caso", nombre, Dni());
        c.Trabajadores.Add(trabajador);
        await c.SaveChangesAsync();
        c.Asignaciones.Add(new Asignacion(trabajador.Id, _centroA1, _hoy.AddDays(-400)));
        foreach (var vigencia in vigencias ?? [])
            c.Documentos.Add(Documento.DeTrabajador(trabajador.Id, _tipoPss, _hoy.AddDays(-400), vigencia));
        await c.SaveChangesAsync();

        var caso = new Caso(nombre, "Trabajador") { TrabajadorId = trabajador.Id };
        caso.PorCentro[_centroA1] = esperado;
        _casos.Add(caso);
    }

    private async Task AnadirR2Async(CaeManagerDbContext c, string nombre, Guid empresaId, bool subcontrata, Esperado? esperado)
    {
        var trabajador = subcontrata
            ? Trabajador.DeSubcontrata(empresaId, "Caso", nombre, Dni())
            : Trabajador.DeEmpresa(empresaId, "Caso", nombre, Dni());
        c.Trabajadores.Add(trabajador);
        await c.SaveChangesAsync();

        var emision = _hoy.AddDays(-400);
        // Documentacion personal COMPLETA: el PSS vigente. R2: bloquea igual.
        c.Documentos.Add(Documento.DeTrabajador(trabajador.Id, _tipoPss, emision, VigenciaDocumento.VenceEl(_hoy.AddDays(200))));
        foreach (var centro in new[] { _centroA1, _centroA2, _centroA3SinGestion })
            c.Asignaciones.Add(new Asignacion(trabajador.Id, centro, emision));
        await c.SaveChangesAsync();

        var caso = new Caso(nombre, "Empresa") { TrabajadorId = trabajador.Id };
        caso.PorCentro[_centroA1] = esperado;
        caso.PorCentro[_centroA2] = esperado;
        caso.PorCentro[_centroA3SinGestion] = null; // un Centro sin gestion CAE no exige nada, tampoco por R2
        _casos.Add(caso);
    }

    /// <summary>
    /// Tenant B: su Empresa no tiene el certificado y el tipo es bloqueante en B. Su Trabajador esta bloqueado en B y
    /// nunca debe verse desde otro Tenant.
    /// </summary>
    private async Task SembrarTenantBAsync()
    {
        using var ambito = AmbitoTenantExplicito.Establecer(_tenantB);
        var c = _propietario;

        var cliente = Empresa.CrearComoCliente("Cliente empresarial B", "B12345674", false, null, null);
        var contratista = new Empresa("Contratista B S.L.");
        c.Empresas.AddRange(cliente, contratista);
        await c.SaveChangesAsync();

        var centro = new Centro(cliente.Id, contratista.Id, "Centro B1");
        var certificado = new TipoDocumento("Certificado de la Seguridad Social", null, false, 1, AmbitoAplicacion.Empresa);
        c.Centros.Add(centro);
        c.TiposDocumento.Add(certificado);
        await c.SaveChangesAsync();
        c.TiposDocumentoCentros.Add(new TipoDocumentoCentro(certificado.Id, centro.Id, incluido: true, bloqueaAcceso: true));

        var trabajador = Trabajador.DeEmpresa(contratista.Id, "Trabajador", "De B", Dni());
        c.Trabajadores.Add(trabajador);
        await c.SaveChangesAsync();
        c.Asignaciones.Add(new Asignacion(trabajador.Id, centro.Id, _hoy.AddDays(-400)));
        await c.SaveChangesAsync();

        _centroB = centro.Id;
        _trabajadorB = trabajador.Id;
    }

    private Guid _centroB, _trabajadorB, _centroC, _trabajadorC;

    /// <summary>
    /// Tenant C: tiene el mismo tipo de certificado, su Empresa tampoco lo tiene, pero el tipo NO es bloqueante en C.
    /// Que sea bloqueante en B no puede alcanzarle: nadie en C debe quedar bloqueado.
    /// </summary>
    private async Task SembrarTenantCAsync()
    {
        using var ambito = AmbitoTenantExplicito.Establecer(_tenantC);
        var c = _propietario;

        var cliente = Empresa.CrearComoCliente("Cliente empresarial C", "B12345674", false, null, null);
        var contratista = new Empresa("Contratista C S.L.");
        c.Empresas.AddRange(cliente, contratista);
        await c.SaveChangesAsync();

        var centro = new Centro(cliente.Id, contratista.Id, "Centro C1");
        var certificado = new TipoDocumento("Certificado de la Seguridad Social", null, false, 1, AmbitoAplicacion.Empresa);
        c.Centros.Add(centro);
        c.TiposDocumento.Add(certificado);
        await c.SaveChangesAsync();

        var trabajador = Trabajador.DeEmpresa(contratista.Id, "Trabajador", "De C", Dni());
        c.Trabajadores.Add(trabajador);
        await c.SaveChangesAsync();
        c.Asignaciones.Add(new Asignacion(trabajador.Id, centro.Id, _hoy.AddDays(-400)));
        await c.SaveChangesAsync();

        _centroC = centro.Id;
        _trabajadorC = trabajador.Id;
    }

    private Guid _trabajadorD, _centroDSinGestion;

    /// <summary>
    /// Tenant D: el certificado de Empresa esta marcado como bloqueante SOLO en un Centro sin gestion CAE. Ese Centro
    /// no exige nada, asi que su marca no declara requisito: el Trabajador (sin certificado en su Empresa) asignado a
    /// otro Centro de D, con gestion CAE, no debe quedar bloqueado por ella.
    /// </summary>
    private async Task SembrarTenantDAsync()
    {
        using var ambito = AmbitoTenantExplicito.Establecer(_tenantD);
        var c = _propietario;

        var cliente = Empresa.CrearComoCliente("Cliente empresarial D", "B12345674", false, null, null);
        var contratista = new Empresa("Contratista D S.L.");
        c.Empresas.AddRange(cliente, contratista);
        await c.SaveChangesAsync();

        var centroConGestion = new Centro(cliente.Id, contratista.Id, "Centro D1");
        var centroSinGestion = new Centro(cliente.Id, contratista.Id, "Centro D2 sin gestion CAE");
        centroSinGestion.EstablecerGestionCae(ModalidadGestionCae.SinGestionCae);
        var certificado = new TipoDocumento("Certificado de la Seguridad Social", null, false, 1, AmbitoAplicacion.Empresa);
        c.Centros.AddRange(centroConGestion, centroSinGestion);
        c.TiposDocumento.Add(certificado);
        await c.SaveChangesAsync();
        c.TiposDocumentoCentros.Add(new TipoDocumentoCentro(certificado.Id, centroSinGestion.Id, incluido: true, bloqueaAcceso: true));

        var trabajador = Trabajador.DeEmpresa(contratista.Id, "Trabajador", "De D", Dni());
        c.Trabajadores.Add(trabajador);
        await c.SaveChangesAsync();
        c.Asignaciones.Add(new Asignacion(trabajador.Id, centroConGestion.Id, _hoy.AddDays(-400)));
        await c.SaveChangesAsync();

        _centroDSinGestion = centroSinGestion.Id;
        _trabajadorD = trabajador.Id;
    }

    // ---------- Lectura bajo RLS ----------

    private CaeManagerDbContext Runtime(Guid tenant)
    {
        var tenantDeLaPeticion = new TenantActualDeLaPeticion(tenant);
        var usuario = new CurrentUserServiceFalso(_usuario, tenantOrigenId: tenant);
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion))
            .AddInterceptors(
                new TenantSelladoInterceptor(tenantDeLaPeticion),
                new TenantRlsConnectionInterceptor(tenantDeLaPeticion, new SinClienteActivo(), usuario, BaseDatosPostgresDePruebas.FirmanteContextoRls))
            .Options;
        var contexto = new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenantDeLaPeticion);
        _contextosRuntime.Add(contexto);
        return contexto;
    }

    private static Task<IReadOnlyList<DocumentacionBloqueantePendienteDto>> MiTrabajo(
        CaeManagerDbContext c, IAlcanceDatosService? alcance = null) =>
        new ObtenerDocumentacionBloqueantePendienteQueryHandler(c, c, c, c, c, c, alcance ?? new AlcanceDatosServiceFalso())
            .Handle(new ObtenerDocumentacionBloqueantePendienteQuery(), CancellationToken.None);

    // ---------- La tabla ----------

    [Fact]
    public async Task Control_la_lectura_va_bajo_RLS_efectiva()
    {
        // Sin este control, «el Tenant A no ve lo de B» pasaria tambien sobre una conexion que se salta RLS.
        var comoA = Runtime(_tenantA);
        (await comoA.Centros.IgnoreQueryFilters().CountAsync(x => x.Id == _centroB)).Should().Be(0);
        (await comoA.Centros.IgnoreQueryFilters().CountAsync(x => x.Id == _centroA1)).Should().Be(1);
    }

    [Fact]
    public void Control_la_siembra_aporta_al_menos_un_bloqueo_y_un_no_bloqueo_de_cada_regla()
    {
        // Que la tabla pueda fallar: sin filas bloqueadas ni no bloqueadas de R1 y de R2, «todo coincide» seria vacio.
        _casos.Where(c => c.Superficie == "Trabajador").SelectMany(c => c.PorCentro.Values).Should().Contain(v => v != null).And.Contain(v => v == null);
        _casos.Where(c => c.Superficie == "Empresa").SelectMany(c => c.PorCentro.Values).Should().Contain(v => v != null).And.Contain(v => v == null);
    }

    [Fact]
    public async Task La_misma_documentacion_da_el_mismo_bloqueo_en_Mi_trabajo_y_en_la_cola()
    {
        var pendientes = await MiTrabajo(Runtime(_tenantA));
        var fallos = new List<string>();

        // Superficie 1: la consulta de Mi trabajo, exactamente lo que dice la tabla.
        foreach (var caso in _casos)
        {
            foreach (var (centro, esperado) in caso.PorCentro)
            {
                var filas = pendientes.Where(p => p.TrabajadorId == caso.TrabajadorId && p.CentroId == centro).ToList();
                if (esperado is null)
                {
                    if (filas.Count != 0)
                        fallos.Add($"{caso.Nombre} · Centro {Nombre(centro)}: no debia estar bloqueado y aparece {Describir(filas)}");
                    continue;
                }

                if (filas.Count != 1)
                {
                    fallos.Add($"{caso.Nombre} · Centro {Nombre(centro)}: esperada 1 fila, observadas {filas.Count} {Describir(filas)}");
                    continue;
                }

                var fila = filas[0];
                if (fila.Ambito != esperado.Ambito || fila.Situacion != esperado.Situacion || fila.EsAltaNueva != esperado.EsAltaNueva)
                    fallos.Add($"{caso.Nombre} · Centro {Nombre(centro)}: esperado {esperado}, observado {Describir([fila])}");

                var tipoEsperado = esperado.Ambito == AmbitoAplicacion.Trabajador ? _tipoPss : _tipoCertificado;
                if (fila.TipoDocumentoId != tipoEsperado)
                    fallos.Add($"{caso.Nombre} · Centro {Nombre(centro)}: el requisito no es el del ambito {esperado.Ambito}");
            }
        }

        // Nada fuera de la tabla: ninguna fila de un Trabajador que la tabla no conozca ni de un tipo sin sujeto (Cliente).
        var conocidos = _casos.Select(c => c.TrabajadorId).ToHashSet();
        foreach (var fila in pendientes.Where(p => !conocidos.Contains(p.TrabajadorId)))
            fallos.Add($"Fila fuera de la tabla: {Describir([fila])}");
        pendientes.Select(p => p.TipoDocumentoId).Distinct().Should().BeSubsetOf([_tipoPss, _tipoCertificado],
            "un tipo de ambito Cliente marcado como bloqueante no tiene sujeto y no bloquea a nadie");

        // Superficie 2: la fusion de la cola de Mi trabajo, que decide el titulo y si bloquea de verdad.
        var cola = ObtenerBandejaGestorQueryHandler.Fusionar([], [], pendientes, [], [], [], [], _hoy, 24, 4);
        cola.Should().HaveCount(pendientes.Count);
        foreach (var fila in pendientes)
        {
            var item = cola.Single(i => i.Id == $"requisito-{fila.CentroId}-{fila.TrabajadorId}-{fila.TipoDocumentoId}");
            var bloqueaDeVerdad = ObtenerBandejaAgrupadaQueryHandler.BloqueaAccesoAlCentro(item);
            if (bloqueaDeVerdad == fila.EsAltaNueva)
                fallos.Add($"Cola · {item.Titulo}: BloqueaAccesoAlCentro={bloqueaDeVerdad} pero EsAltaNueva={fila.EsAltaNueva}");

            if (fila.Ambito == AmbitoAplicacion.Empresa)
            {
                item.Titulo.Should().Contain("(Empresa ").And.Contain(fila.EmpresaNombre!,
                    "el titulo dice que el documento que falta es de la Empresa, no del Trabajador");
                fila.EsAltaNueva.Should().BeFalse("un Trabajador bloqueado por su Empresa no es un alta sin completar");
            }
            else
            {
                item.Titulo.Should().NotContain("(Empresa ");
            }
        }

        fallos.Should().BeEmpty("todas las superficies tienen que dar lo mismo que la tabla:\n" + string.Join("\n", fallos));
    }

    [Fact]
    public async Task R2_un_requisito_de_Empresa_marcado_en_un_Centro_no_visible_bloquea_igual_en_los_Centros_visibles()
    {
        // El tipo esta marcado SOLO en el Centro A1; un Gestor CAE que ve solo el A2 tiene que ver bloqueados a los
        // Trabajadores de la Empresa sin certificado. El alcance limita lo que se devuelve, no si el requisito existe.
        var pendientes = await MiTrabajo(Runtime(_tenantA), new AlcanceDatosServiceFalso(centroIds: [_centroA2]));

        pendientes.Should().NotBeEmpty();
        pendientes.Select(p => p.CentroId).Distinct().Should().Equal([_centroA2], "solo el Centro visible");

        var ausente = _casos.Single(c => c.Nombre == "R2 · Empresa sin el certificado");
        pendientes.Should().ContainSingle(p => p.TrabajadorId == ausente.TrabajadorId && p.Ambito == AmbitoAplicacion.Empresa);

        // Y no se cuela nada de Centros fuera de su alcance, ni del requisito de Trabajador marcado en el A1.
        pendientes.Should().NotContain(p => p.Ambito == AmbitoAplicacion.Trabajador);
    }

    [Fact]
    public async Task Aislamiento_cada_Tenant_ve_solo_lo_suyo_y_un_requisito_de_otro_Tenant_no_bloquea_a_nadie()
    {
        // B: su Trabajador esta bloqueado por el certificado de su Empresa y solo B lo ve.
        var deB = await MiTrabajo(Runtime(_tenantB));
        var filaB = deB.Should().ContainSingle().Subject;
        filaB.TrabajadorId.Should().Be(_trabajadorB);
        filaB.CentroId.Should().Be(_centroB);
        filaB.Ambito.Should().Be(AmbitoAplicacion.Empresa);

        // C: el tipo es bloqueante en B pero no en C; el Trabajador de C, cuya Empresa tampoco tiene el certificado,
        // no esta bloqueado. Si la designacion cruzara Tenants, apareceria aqui.
        (await MiTrabajo(Runtime(_tenantC))).Should().BeEmpty(
            "el requisito de Empresa que es bloqueante en el Tenant B no alcanza a los Trabajadores del Tenant C");

        // A: no ve nada de B ni de C.
        var deA = await MiTrabajo(Runtime(_tenantA));
        deA.Should().NotContain(p => p.TrabajadorId == _trabajadorB || p.TrabajadorId == _trabajadorC);
        deA.Should().NotContain(p => p.CentroId == _centroB || p.CentroId == _centroC);
        deA.Should().NotBeEmpty("control: A si tiene bloqueos propios");
    }

    [Fact]
    public async Task Un_tipo_de_Empresa_marcado_solo_en_un_Centro_sin_gestion_CAE_no_declara_requisito()
    {
        // La marca de un Centro sin gestion CAE no cuenta (ese Centro no exige nada): no se extiende a los demas Centros.
        var comoD = Runtime(_tenantD);

        // Controles positivos bajo RLS: la marca existe y el Trabajador esta asignado a un Centro con gestion CAE. Sin
        // ellos, «vacio» pasaria tambien si la siembra no fuera visible.
        (await comoD.TiposDocumentoCentros.CountAsync(x => x.CentroId == _centroDSinGestion && x.Incluido && x.BloqueaAcceso)).Should().Be(1);
        (await comoD.Asignaciones.CountAsync(a => a.TrabajadorId == _trabajadorD && a.FechaBaja == null)).Should().Be(1);

        (await MiTrabajo(comoD)).Should().BeEmpty(
            "el unico Centro que marca el certificado como bloqueante no tiene gestion CAE");
    }

    private string Nombre(Guid centro) =>
        centro == _centroA1 ? "A1" : centro == _centroA2 ? "A2" : centro == _centroA3SinGestion ? "A3 (sin gestion CAE)" : centro.ToString();

    private static string Describir(IEnumerable<DocumentacionBloqueantePendienteDto> filas) =>
        "[" + string.Join("; ", filas.Select(f => $"{f.Ambito}/{f.Situacion}/altaNueva={f.EsAltaNueva}")) + "]";

    private sealed class TenantActualPorAmbito : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual;
    }

    private sealed class TenantActualDeLaPeticion(Guid tenantDeLaSesion) : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual ?? tenantDeLaSesion;
    }

    private sealed class SinClienteActivo : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }
}
