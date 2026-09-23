using CaeManager.Application.Documentos.Acreditacion;
using CaeManager.Application.Documentos.Commands.CrearDocumento;
using CaeManager.Application.Documentos.Queries.ObtenerAcreditacionesPorProveedor;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.DocumentosIa;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Documentos;

/// <summary>
/// Incremento 2 del MVP1 de extensión de navegador (ver
/// ARQUITECTURA-INTEGRACIONES.md § 14 en el repositorio de negocio):
/// <see cref="ObtenerAcreditacionesPorProveedorQuery"/> no tenía ningún test
/// antes de este incremento — se cubre aquí tanto su comportamiento ya
/// existente (agrupación, alcance) como los dos campos nuevos que la
/// extensión necesita (<see cref="AcreditacionDrillDownDto.CanalGestionDocumentalId"/>,
/// <see cref="AcreditacionDrillDownDto.TrabajadorDni"/>).
/// </summary>
public class ObtenerAcreditacionesPorProveedorQueryTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly TenantActualAmbiental _tenantActual = new() { TenantId = Guid.NewGuid() };

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Incluye_el_canal_y_el_dni_del_trabajador_para_una_acreditacion_pendiente()
    {
        Guid trabajadorId, tipoDocumentoId, canalId, proveedorId;
        await using (var contexto = CrearContexto())
        {
            var cliente = Empresa.CrearComoCliente("Cliente Extensión S.L.", "B10380194", false, null, null);
            var empresa = new Empresa("Empresa Extensión S.L.", "B10380186");
            contexto.Empresas.Add(cliente);
            contexto.Empresas.Add(empresa);
            await contexto.SaveChangesAsync();

            var centro = new Centro(cliente.Id, empresa.Id, "Centro Extensión");
            contexto.Centros.Add(centro);
            await contexto.SaveChangesAsync();

            var proveedor = await contexto.ProveedoresPlataformaCae.FirstAsync();
            var canalPlataforma = CanalGestionDocumental.DePlataforma(
                centro.Id, "Gestión general", proveedor.Id, "https://plataforma.test", "usuario", "clave");
            contexto.CanalesGestionDocumental.Add(canalPlataforma);

            var trabajador = Trabajador.DeEmpresa(empresa.Id, "Nora", "Vidal", "22334455Y");
            contexto.Trabajadores.Add(trabajador);
            await contexto.SaveChangesAsync();

            contexto.Asignaciones.Add(new Asignacion(trabajador.Id, centro.Id, new DateOnly(2026, 1, 1)));
            var tipoDocumento = new TipoDocumento("Apto médico", 12, true, 1, AmbitoAplicacion.Trabajador);
            contexto.TiposDocumento.Add(tipoDocumento);
            await contexto.SaveChangesAsync();

            trabajadorId = trabajador.Id;
            tipoDocumentoId = tipoDocumento.Id;
            canalId = canalPlataforma.Id;
            proveedorId = proveedor.Id;
        }

        await using (var contexto = CrearContexto())
        {
            var handler = new CrearDocumentoCommandHandler(
                new DocumentoRepository(contexto), contexto, contexto, contexto, contexto, contexto,
                contexto, new ColaAnalisisDocumentoFalsa(), new CurrentUserServiceFalso(),
                new DerivarCanalesAplicablesDocumentoService(contexto, contexto, contexto),
                new AcreditacionDocumentoPlataformaRepository(contexto), new PublisherFalso(), new AlcanceDatosServiceFalso());

            var resultado = await handler.Handle(
                new CrearDocumentoCommand(
                    TrabajadorId: trabajadorId, ClienteId: null, EmpresaId: null, VehiculoId: null, ProyectoId: null,
                    TipoDocumentoId: tipoDocumentoId, FechaEmision: new DateOnly(2026, 1, 1),
                    FechaVencimientoManual: null, ArchivoUrl: null, Comentarios: null),
                CancellationToken.None);

            resultado.EsExitoso.Should().BeTrue();
        }

        await using var consulta = CrearContexto();
        var handlerQuery = new ObtenerAcreditacionesPorProveedorQueryHandler(
            consulta, consulta, consulta, consulta, consulta, consulta, new AlcanceDatosServiceFalso());

        var proveedores = await handlerQuery.Handle(new ObtenerAcreditacionesPorProveedorQuery(), CancellationToken.None);

        var proveedorResultado = proveedores.Should().ContainSingle().Subject;
        proveedorResultado.ProveedorPlataformaCaeId.Should().Be(proveedorId);

        var acreditacion = proveedorResultado.Clientes.Should().ContainSingle().Subject
            .Documentos.Should().ContainSingle().Subject;

        acreditacion.CanalGestionDocumentalId.Should().Be(canalId);
        acreditacion.TrabajadorDni.Should().Be("22334455Y");
        acreditacion.TrabajadorId.Should().Be(trabajadorId);
    }

    /// <summary>
    /// Las dos mitades del mismo contrato, en una sola prueba porque separarlas
    /// dejaría cada lado verde por su cuenta sin garantizar que se excluyen: la
    /// extensión de navegador y la Bandeja piden lo que falta subir y NO deben
    /// ver las aceptadas —ofrecerían subir otra vez algo ya acreditado—,
    /// mientras que el drill-down por plataforma sí las necesita, porque es la
    /// única pantalla desde la que se puede anotar hasta cuándo vale un
    /// documento allí.
    /// </summary>
    [Fact]
    public async Task Una_aceptada_solo_sale_cuando_se_piden_las_aceptadas()
    {
        Guid acreditacionId;
        var vence = new DateOnly(2027, 4, 30);

        await using (var contexto = CrearContexto())
        {
            var cliente = Empresa.CrearComoCliente("Cliente Vigencia S.L.", "B10380194", false, null, null);
            var empresa = new Empresa("Empresa Vigencia S.L.", "B10380186");
            contexto.Empresas.Add(cliente);
            contexto.Empresas.Add(empresa);
            await contexto.SaveChangesAsync();

            var centro = new Centro(cliente.Id, empresa.Id, "Centro Vigencia");
            contexto.Centros.Add(centro);
            await contexto.SaveChangesAsync();

            var proveedor = await contexto.ProveedoresPlataformaCae.FirstAsync();
            var canal = CanalGestionDocumental.DePlataforma(
                centro.Id, "Gestión general", proveedor.Id, null, null, null);
            contexto.CanalesGestionDocumental.Add(canal);

            var tipoDocumento = new TipoDocumento("Seguro RC vigencia", 12, true, 1, AmbitoAplicacion.Empresa);
            contexto.TiposDocumento.Add(tipoDocumento);
            await contexto.SaveChangesAsync();

            var documento = Documento.DeEmpresa(
                empresa.Id, tipoDocumento.Id, new DateOnly(2026, 1, 1), new DateOnly(2027, 1, 1));
            contexto.Documentos.Add(documento);
            await contexto.SaveChangesAsync();

            var acreditacion = new AcreditacionDocumentoPlataforma(documento.Id, canal.Id);
            acreditacion.MarcarAceptada(VigenciaEnPlataforma.VenceEl(vence));
            contexto.AcreditacionesDocumentoPlataforma.Add(acreditacion);
            await contexto.SaveChangesAsync();

            acreditacionId = acreditacion.Id;
        }

        await using var consulta = CrearContexto();
        var handler = new ObtenerAcreditacionesPorProveedorQueryHandler(
            consulta, consulta, consulta, consulta, consulta, consulta, new AlcanceDatosServiceFalso());

        var porDefecto = await handler.Handle(
            new ObtenerAcreditacionesPorProveedorQuery(), CancellationToken.None);

        porDefecto.SelectMany(p => p.Clientes).SelectMany(c => c.Documentos)
            .Should().NotContain(d => d.AcreditacionId == acreditacionId);

        var conAceptadas = await handler.Handle(
            new ObtenerAcreditacionesPorProveedorQuery(IncluirAceptadas: true), CancellationToken.None);

        var fila = conAceptadas.SelectMany(p => p.Clientes).SelectMany(c => c.Documentos)
            .Should().ContainSingle(d => d.AcreditacionId == acreditacionId).Subject;

        // La vigencia viaja en el DTO, o la pantalla no podría decir qué hay
        // anotado ni ofrecer corregirlo.
        fila.EstadoVigencia.Should().Be(EstadoVigenciaEnPlataforma.VenceEnFecha);
        fila.FechaVencimientoEnPlataforma.Should().Be(vence);
    }

    /// <summary>
    /// P12 (2026-09-23): <c>IncluirVencidasEnPlataforma</c> trae, además de lo
    /// pendiente, solo las aceptadas cuya vigencia en la plataforma ya pasó, y
    /// las marca con <see cref="AcreditacionDrillDownDto.VencidaEnPlataforma"/>
    /// usando la misma fecha con la que las eligió. La frontera se prueba aquí,
    /// contra PostgreSQL, porque es la consulta la que compara: Mi trabajo se
    /// fía del indicador y no vuelve a leer el reloj.
    /// </summary>
    [Fact]
    public async Task Las_vencidas_en_plataforma_solo_salen_cuando_se_piden_y_llevan_el_indicador()
    {
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var ids = new Dictionary<string, Guid>();

        await using (var contexto = CrearContexto())
        {
            var cliente = Empresa.CrearComoCliente("Cliente Vencidas S.L.", "B10380194", false, null, null);
            var empresa = new Empresa("Empresa Vencidas S.L.", "B10380186");
            contexto.Empresas.Add(cliente);
            contexto.Empresas.Add(empresa);
            await contexto.SaveChangesAsync();

            var centro = new Centro(cliente.Id, empresa.Id, "Centro Vencidas");
            contexto.Centros.Add(centro);
            await contexto.SaveChangesAsync();

            var proveedor = await contexto.ProveedoresPlataformaCae.FirstAsync();
            var canal = CanalGestionDocumental.DePlataforma(
                centro.Id, "Gestión general", proveedor.Id, null, null, null);
            contexto.CanalesGestionDocumental.Add(canal);
            await contexto.SaveChangesAsync();

            async Task SembrarAsync(string clave, Action<AcreditacionDocumentoPlataforma> estado)
            {
                var tipo = new TipoDocumento($"Tipo {clave}", 12, true, 1, AmbitoAplicacion.Empresa);
                contexto.TiposDocumento.Add(tipo);
                await contexto.SaveChangesAsync();

                var documento = Documento.DeEmpresa(
                    empresa.Id, tipo.Id, new DateOnly(2026, 1, 1), new DateOnly(2030, 1, 1));
                contexto.Documentos.Add(documento);
                await contexto.SaveChangesAsync();

                var acreditacion = new AcreditacionDocumentoPlataforma(documento.Id, canal.Id);
                estado(acreditacion);
                contexto.AcreditacionesDocumentoPlataforma.Add(acreditacion);
                await contexto.SaveChangesAsync();
                ids[clave] = acreditacion.Id;
            }

            await SembrarAsync("vencida", a => a.MarcarAceptada(VigenciaEnPlataforma.VenceEl(hoy.AddDays(-1))));
            await SembrarAsync("vence-hoy", a => a.MarcarAceptada(VigenciaEnPlataforma.VenceEl(hoy)));
            await SembrarAsync("vigente", a => a.MarcarAceptada(VigenciaEnPlataforma.VenceEl(hoy.AddDays(30))));
            await SembrarAsync("sin-confirmar", a => a.MarcarAceptada(VigenciaEnPlataforma.SinConfirmar));
            // Reenviada tras aceptarse: MarcarSubida no reinicia la vigencia, así
            // que conserva la fecha pasada; sigue siendo Seguimiento, no vencida.
            await SembrarAsync("subida-con-fecha-pasada", a =>
            {
                a.MarcarAceptada(VigenciaEnPlataforma.VenceEl(hoy.AddDays(-10)));
                a.MarcarSubida();
            });
        }

        await using var consulta = CrearContexto();
        var handler = new ObtenerAcreditacionesPorProveedorQueryHandler(
            consulta, consulta, consulta, consulta, consulta, consulta, new AlcanceDatosServiceFalso());

        async Task<List<AcreditacionDrillDownDto>> PedirAsync(ObtenerAcreditacionesPorProveedorQuery query) =>
            (await handler.Handle(query, CancellationToken.None))
                .SelectMany(p => p.Clientes).SelectMany(c => c.Documentos)
                .Where(d => ids.ContainsValue(d.AcreditacionId))
                .ToList();

        // Por defecto (extensión, /bandeja, Inicio): ninguna aceptada ni subida.
        (await PedirAsync(new ObtenerAcreditacionesPorProveedorQuery())).Should().BeEmpty();

        // Lo que pide Mi trabajo: solo la vencida, marcada.
        var vencidas = await PedirAsync(new ObtenerAcreditacionesPorProveedorQuery(IncluirVencidasEnPlataforma: true));
        var vencida = vencidas.Should().ContainSingle().Subject;
        vencida.AcreditacionId.Should().Be(ids["vencida"]);
        vencida.VencidaEnPlataforma.Should().BeTrue();

        // Con todas las aceptadas (drill-down por plataforma), el indicador solo
        // está en la vencida: la que vence hoy aún vale.
        var aceptadas = await PedirAsync(new ObtenerAcreditacionesPorProveedorQuery(IncluirAceptadas: true));
        aceptadas.Select(d => d.AcreditacionId).Should().BeEquivalentTo(
            new[] { ids["vencida"], ids["vence-hoy"], ids["vigente"], ids["sin-confirmar"] });
        aceptadas.Where(d => d.VencidaEnPlataforma).Select(d => d.AcreditacionId)
            .Should().Equal(ids["vencida"]);
    }

    /// <summary>
    /// Regresión de H-D1 (piloto Outbound): «Marcar subido» dejaba la
    /// acreditación en <c>Subida</c> y ninguna consulta la devolvía, así que
    /// la fila desaparecía del drill-down y no se podía anotar la respuesta de
    /// la plataforma (aceptada o rechazada). Por defecto siguen sin salir —
    /// la Bandeja y la extensión no la piden: no es trabajo pendiente—, y
    /// solo el drill-down las pide con <c>IncluirSubidas</c>.
    /// </summary>
    [Fact]
    public async Task Una_subida_solo_sale_cuando_se_piden_las_subidas()
    {
        Guid subidaId;
        Guid rechazadaId;

        await using (var contexto = CrearContexto())
        {
            var cliente = Empresa.CrearComoCliente("Cliente Subida S.L.", "B10380194", false, null, null);
            var empresa = new Empresa("Empresa Subida S.L.", "B10380186");
            contexto.Empresas.Add(cliente);
            contexto.Empresas.Add(empresa);
            await contexto.SaveChangesAsync();

            var centro = new Centro(cliente.Id, empresa.Id, "Centro Subida");
            contexto.Centros.Add(centro);
            await contexto.SaveChangesAsync();

            var proveedor = await contexto.ProveedoresPlataformaCae.FirstAsync();
            var canal = CanalGestionDocumental.DePlataforma(
                centro.Id, "Gestión general", proveedor.Id, null, null, null);
            contexto.CanalesGestionDocumental.Add(canal);

            var tipoUno = new TipoDocumento("Seguro RC subida", 12, true, 1, AmbitoAplicacion.Empresa);
            var tipoDos = new TipoDocumento("Certificado subida", 12, true, 1, AmbitoAplicacion.Empresa);
            contexto.TiposDocumento.Add(tipoUno);
            contexto.TiposDocumento.Add(tipoDos);
            await contexto.SaveChangesAsync();

            var documentoUno = Documento.DeEmpresa(empresa.Id, tipoUno.Id, new DateOnly(2026, 1, 1), new DateOnly(2027, 1, 1));
            var documentoDos = Documento.DeEmpresa(empresa.Id, tipoDos.Id, new DateOnly(2026, 1, 1), new DateOnly(2027, 1, 1));
            contexto.Documentos.Add(documentoUno);
            contexto.Documentos.Add(documentoDos);
            await contexto.SaveChangesAsync();

            var subida = new AcreditacionDocumentoPlataforma(documentoUno.Id, canal.Id);
            subida.MarcarSubida();
            var rechazada = new AcreditacionDocumentoPlataforma(documentoDos.Id, canal.Id);
            rechazada.Rechazar(CausaRechazoAcreditacion.Otro, "Escaneo ilegible", DateTime.UtcNow);
            contexto.AcreditacionesDocumentoPlataforma.Add(subida);
            contexto.AcreditacionesDocumentoPlataforma.Add(rechazada);
            await contexto.SaveChangesAsync();

            subidaId = subida.Id;
            rechazadaId = rechazada.Id;
        }

        await using var consulta = CrearContexto();
        var handler = new ObtenerAcreditacionesPorProveedorQueryHandler(
            consulta, consulta, consulta, consulta, consulta, consulta, new AlcanceDatosServiceFalso());

        var porDefecto = (await handler.Handle(new ObtenerAcreditacionesPorProveedorQuery(), CancellationToken.None))
            .SelectMany(p => p.Clientes).SelectMany(c => c.Documentos).ToList();

        porDefecto.Should().NotContain(d => d.AcreditacionId == subidaId);
        porDefecto.Should().ContainSingle(d => d.AcreditacionId == rechazadaId)
            .Which.UltimoMotivoRechazo.Should().Be("Escaneo ilegible");

        var conSubidas = (await handler.Handle(
                new ObtenerAcreditacionesPorProveedorQuery(IncluirSubidas: true), CancellationToken.None))
            .SelectMany(p => p.Clientes).SelectMany(c => c.Documentos).ToList();

        conSubidas.Should().ContainSingle(d => d.AcreditacionId == subidaId)
            .Which.Estado.Should().Be(EstadoAcreditacion.Subida);
        conSubidas.Should().Contain(d => d.AcreditacionId == rechazadaId);
    }

    [Fact]
    public async Task No_devuelve_nada_fuera_de_la_cartera_del_gestor()
    {
        Guid centroId, proveedorId;
        await using (var contexto = CrearContexto())
        {
            var cliente = Empresa.CrearComoCliente("Cliente Fuera De Cartera S.L.", "B10380194", false, null, null);
            var empresa = new Empresa("Empresa Fuera De Cartera S.L.", "B10380186");
            contexto.Empresas.Add(cliente);
            contexto.Empresas.Add(empresa);
            await contexto.SaveChangesAsync();

            var centro = new Centro(cliente.Id, empresa.Id, "Centro Fuera De Cartera");
            contexto.Centros.Add(centro);
            await contexto.SaveChangesAsync();

            var proveedor = await contexto.ProveedoresPlataformaCae.FirstAsync();
            var canalPlataforma = CanalGestionDocumental.DePlataforma(
                centro.Id, "Gestión general", proveedor.Id, "https://plataforma.test", "usuario", "clave");
            contexto.CanalesGestionDocumental.Add(canalPlataforma);

            var trabajador = Trabajador.DeEmpresa(empresa.Id, "Iker", "Soto", "99887766P");
            contexto.Trabajadores.Add(trabajador);
            await contexto.SaveChangesAsync();

            contexto.Asignaciones.Add(new Asignacion(trabajador.Id, centro.Id, new DateOnly(2026, 1, 1)));
            var tipoDocumento = new TipoDocumento("Formación", 12, true, 1, AmbitoAplicacion.Trabajador);
            contexto.TiposDocumento.Add(tipoDocumento);
            await contexto.SaveChangesAsync();

            var handlerCrear = new CrearDocumentoCommandHandler(
                new DocumentoRepository(contexto), contexto, contexto, contexto, contexto, contexto,
                contexto, new ColaAnalisisDocumentoFalsa(), new CurrentUserServiceFalso(),
                new DerivarCanalesAplicablesDocumentoService(contexto, contexto, contexto),
                new AcreditacionDocumentoPlataformaRepository(contexto), new PublisherFalso(), new AlcanceDatosServiceFalso());

            await handlerCrear.Handle(
                new CrearDocumentoCommand(
                    TrabajadorId: trabajador.Id, ClienteId: null, EmpresaId: null, VehiculoId: null, ProyectoId: null,
                    TipoDocumentoId: tipoDocumento.Id, FechaEmision: new DateOnly(2026, 1, 1),
                    FechaVencimientoManual: null, ArchivoUrl: null, Comentarios: null),
                CancellationToken.None);

            centroId = centro.Id;
            proveedorId = proveedor.Id;
        }

        // Un GestorCae cuya cartera no incluye este Centro: alcance vacío, no
        // null — el mismo contrato que usa la implementación real para "no
        // tiene nada asignado todavía", nunca "sin restricción".
        await using var consulta = CrearContexto();
        var handlerQuery = new ObtenerAcreditacionesPorProveedorQueryHandler(
            consulta, consulta, consulta, consulta, consulta, consulta,
            new AlcanceDatosServiceFalso(centroIds: []));

        var proveedores = await handlerQuery.Handle(new ObtenerAcreditacionesPorProveedorQuery(), CancellationToken.None);

        proveedores.Should().BeEmpty();
        _ = centroId;
        _ = proveedorId;
    }

    private sealed class ColaAnalisisDocumentoFalsa : ITrabajoAnalisisDocumentoRepository
    {
        public void Agregar(TrabajoAnalisisDocumento trabajo)
        {
        }

        public Task<TrabajoAnalisisDocumento?> ObtenerSiguientePendienteAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<TrabajoAnalisisDocumento?>(null);

        public Task<TrabajoAnalisisDocumento?> ReclamarSiguientePendienteAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<TrabajoAnalisisDocumento?>(null);

        public Task<IReadOnlyList<TrabajoAnalisisDocumento>> ObtenerEstancadosAsync(
            TimeSpan umbral, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TrabajoAnalisisDocumento>>([]);

        public Task<int> ContarActivosAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }

    private CaeManagerDbContext CrearContexto()
    {
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(_tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), _tenantActual);
    }
}
