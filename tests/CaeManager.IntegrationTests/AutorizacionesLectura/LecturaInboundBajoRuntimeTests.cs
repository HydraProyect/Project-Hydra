using CaeManager.Application.Common;
using CaeManager.Application.ContratosAcceso.LecturaInbound;
using CaeManager.Domain.AutorizacionesLectura;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.ContratosAcceso;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Tenants;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.AutorizacionesLectura;

/// <summary>
/// Lectura entre Tenants de Inbound M1 (PR 3) bajo <c>cae_app_runtime</c> con contexto firmado, por
/// <see cref="LectorDocumentosInboundEnBase"/> y las funciones <c>app_inbound_*</c>. X es el Tenant del Cliente empresarial
/// (lector, dueño de los contratos de acceso); Y, el Tenant de la Empresa contratista (Tenant propietario de los Documentos
/// y de las autorizaciones); W, un destinatario que no es X; Z, otro Tenant de Cliente empresarial que vincula por su
/// cuenta un contrato suyo con Y. Lo que prueba la base: solo las dos mitades del apretón juntas dan lectura, y solo del
/// alcance autorizado (sin sustituidos, anonimizados ni eliminados, y sin los de Empresa si la autorización no los
/// incluye); nunca un tipo de categoría especial de salud; el Tenant lector y el usuario salen del token, no de la GUC;
/// cada lectura deja su fila en Y, con el Tenant de origen del usuario, y el runtime no la escribe; el lector no llama
/// dentro de una transacción; y la vigencia del contrato y de la autorización en SQL es la de
/// <see cref="ContratoAcceso.VigenteEn"/> y <see cref="AutorizacionLecturaDocumental.VigenteEn"/>.
/// </summary>
public class LecturaInboundBajoRuntimeTests : IAsyncLifetime
{
    private readonly string _cadena = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Tenant _lector = new("Tenant del Cliente empresarial");
    private readonly Tenant _contratista = new("Tenant de la Empresa contratista");
    private readonly Tenant _otroLector = new("Tenant de otro Cliente empresarial");
    private readonly Guid _tercero = Guid.NewGuid();
    private readonly Guid _usuarioLector = Guid.NewGuid();
    private readonly Guid _usuarioContratista = Guid.NewGuid();
    private readonly Guid _usuarioOtroLector = Guid.NewGuid();
    private readonly DateOnly _hoy = DiaDeNegocio.Hoy();

    // Contratos de X. _bordes lleva cada contrato que tiene autorización vigente en Y, para comparar con VigenteEn.
    private readonly List<ContratoAcceso> _bordes = [];
    private ContratoAcceso _vigente = null!;
    private ContratoAcceso _sinVinculo = null!;
    private ContratoAcceso _sinAutorizacion = null!;
    private ContratoAcceso _revocada = null!;
    private ContratoAcceso _caducada = null!;
    private ContratoAcceso _otroDestinatario = null!;
    private ContratoAcceso _cerrado = null!;
    private ContratoAcceso _eliminado = null!;
    private ContratoAcceso _futuro = null!;
    private ContratoAcceso _sinEmpresa = null!;
    private ContratoAcceso _autHastaHoy = null!;
    private ContratoAcceso _autDesdeHoy = null!;
    private ContratoAcceso _autDesdeManana = null!;

    // Contrato de Z vinculado por su cuenta con Y, sin que Y autorice nada a Z.
    private ContratoAcceso _deOtroLector = null!;

    // Las autorizaciones de los bordes de vigencia, para comparar con AutorizacionLecturaDocumental.VigenteEn.
    private readonly List<(ContratoAcceso Contrato, AutorizacionLecturaDocumental Autorizacion)> _vigenciasAutorizacion = [];

    // Documentos de Y.
    private Guid _autorizacion;
    private Guid _sustituido;
    private Guid _anonimizadoDoc;
    private Guid _deTrabajadorAnonimizado;
    private Guid _deEmpresaPropiaEliminada;
    private Guid _deEmpresa;
    private Guid _deTrabajador;
    private Guid _deTrabajadorFuera;
    private Guid _deTrabajadorEliminado;
    private Guid _salud;
    private Guid _eliminadoDoc;
    private Guid _deContraparte;

    public async Task InitializeAsync()
    {
        await using (var contexto = Contexto(_lector.Id))
        {
            await contexto.Database.MigrateAsync();
            contexto.Tenants.AddRange(_lector, _contratista, _otroLector);
            var roles = await contexto.Roles.ToDictionaryAsync(r => r.Name!, r => r.Id);
            Cuenta(contexto, _usuarioLector, _lector.Id, roles[Roles.GestorCae]);
            Cuenta(contexto, _usuarioContratista, _contratista.Id, roles[Roles.Administrador]);
            Cuenta(contexto, _usuarioOtroLector, _otroLector.Id, roles[Roles.Administrador]);

            var cliente = Empresa.CrearComoCliente("Cliente empresarial", "B10380186", false, null, null);
            var contratistaLocal = new Empresa("Empresa contratista", "B10380194");
            contexto.Empresas.AddRange(cliente, contratistaLocal);
            var centro = new Centro(cliente.Id, cliente.Id, "Centro de trabajo del Cliente empresarial");
            contexto.Centros.Add(centro);
            await contexto.SaveChangesAsync();

            ContratoAcceso Contrato(DateOnly inicio, DateOnly? fin = null, bool vincular = true)
            {
                var c = ContratoAcceso.Crear(cliente.Id, centro.Id, contratistaLocal.Id, inicio, fin, null);
                if (vincular) c.VincularTenantContratista(_contratista.Id);
                contexto.ContratosAcceso.Add(c);
                return c;
            }

            _vigente = Contrato(_hoy.AddDays(-10));
            _sinVinculo = Contrato(_hoy.AddDays(-10), vincular: false);
            _sinAutorizacion = Contrato(_hoy.AddDays(-10));
            _revocada = Contrato(_hoy.AddDays(-10));
            _caducada = Contrato(_hoy.AddDays(-10));
            _otroDestinatario = Contrato(_hoy.AddDays(-10));
            _cerrado = Contrato(_hoy.AddDays(-10));
            _cerrado.Cerrar(_hoy.AddDays(-1));
            _eliminado = Contrato(_hoy.AddDays(-10));
            _eliminado.MarcarComoEliminado(_usuarioLector);
            _futuro = Contrato(_hoy.AddDays(1));
            var cierreHoy = Contrato(_hoy.AddDays(-10));
            cierreHoy.Cerrar(_hoy);
            _bordes.AddRange([
                _vigente, _cerrado, _eliminado, _futuro, cierreHoy,
                Contrato(_hoy), Contrato(_hoy.AddDays(-10), _hoy), Contrato(_hoy.AddDays(-10), _hoy.AddDays(-1))]);
            _sinEmpresa = Contrato(_hoy.AddDays(-10));
            _autHastaHoy = Contrato(_hoy.AddDays(-10));
            _autDesdeHoy = Contrato(_hoy.AddDays(-10));
            _autDesdeManana = Contrato(_hoy.AddDays(-10));
            await contexto.SaveChangesAsync();
        }

        await using (var contexto = Contexto(_otroLector.Id))
        {
            var cliente = Empresa.CrearComoCliente("Otro Cliente empresarial", "B10380210", false, null, null);
            var contratistaLocal = new Empresa("Empresa contratista", "B10380194");
            contexto.Empresas.AddRange(cliente, contratistaLocal);
            var centro = new Centro(cliente.Id, cliente.Id, "Centro de trabajo del otro Cliente empresarial");
            contexto.Centros.Add(centro);
            await contexto.SaveChangesAsync();
            _deOtroLector = ContratoAcceso.Crear(cliente.Id, centro.Id, contratistaLocal.Id, _hoy.AddDays(-10), null, null);
            _deOtroLector.VincularTenantContratista(_contratista.Id);
            contexto.ContratosAcceso.Add(_deOtroLector);
            await contexto.SaveChangesAsync();
        }

        await using (var contexto = Contexto(_contratista.Id))
        {
            var propia = new Empresa("Empresa contratista", "B10380194");
            var contraparte = Empresa.CrearComoCliente("Empresa contraparte de la contratista", "B10380202", false, null, null);
            var propiaEliminada = new Empresa("Empresa contratista anterior", "B10380228");
            contexto.Empresas.AddRange(propia, contraparte, propiaEliminada);
            var general = new TipoDocumento("Formación PRL", null, false, 1, AmbitoAplicacion.Trabajador, sensibilidad: SensibilidadDocumental.DatosPersonales);
            var deEmpresa = new TipoDocumento("Seguro de responsabilidad civil", null, false, 2, AmbitoAplicacion.Empresa, sensibilidad: SensibilidadDocumental.SinDatosPersonales);
            var salud = new TipoDocumento("Reconocimiento médico", null, false, 3, AmbitoAplicacion.Trabajador);
            contexto.TiposDocumento.AddRange(general, deEmpresa, salud);
            await contexto.SaveChangesAsync();

            var asignado = Trabajador.DeEmpresa(propia.Id, "Ana", "Pérez", "12345678Z");
            var fuera = Trabajador.DeEmpresa(propia.Id, "Luis", "García", "00000000T");
            var eliminado = Trabajador.DeEmpresa(propia.Id, "Eva", "Ruiz", "00000001R");
            var anonimizado = Trabajador.DeEmpresa(propia.Id, "Iker", "Sanz", "00000002W");
            contexto.Trabajadores.AddRange(asignado, fuera, eliminado, anonimizado);
            await contexto.SaveChangesAsync();

            var documentos = new[]
            {
                Documento.DeEmpresa(propia.Id, deEmpresa.Id, _hoy.AddMonths(-1), VigenciaDocumento.NoCaduca, "y/empresa.pdf"),
                Documento.DeTrabajador(asignado.Id, general.Id, _hoy.AddMonths(-2), VigenciaDocumento.VenceEl(_hoy.AddYears(1)), "y/ana.pdf"),
                Documento.DeTrabajador(fuera.Id, general.Id, _hoy.AddMonths(-2), VigenciaDocumento.NoCaduca, "y/luis.pdf"),
                Documento.DeTrabajador(eliminado.Id, general.Id, _hoy.AddMonths(-2), VigenciaDocumento.NoCaduca, "y/eva.pdf"),
                Documento.DeTrabajador(asignado.Id, salud.Id, _hoy.AddMonths(-3), VigenciaDocumento.NoCaduca, "y/ana-salud.pdf"),
                Documento.DeTrabajador(asignado.Id, general.Id, _hoy.AddMonths(-4), VigenciaDocumento.NoCaduca, "y/ana-borrado.pdf"),
                Documento.DeEmpresa(contraparte.Id, deEmpresa.Id, _hoy.AddMonths(-1), VigenciaDocumento.NoCaduca, "y/contraparte.pdf"),
                Documento.DeTrabajador(asignado.Id, general.Id, _hoy.AddMonths(-14), VigenciaDocumento.NoCaduca, "y/ana-anterior.pdf"),
                Documento.DeTrabajador(asignado.Id, general.Id, _hoy.AddMonths(-5), VigenciaDocumento.NoCaduca, "y/ana-anonimizado.pdf"),
                Documento.DeTrabajador(anonimizado.Id, general.Id, _hoy.AddMonths(-2), VigenciaDocumento.NoCaduca, "y/iker.pdf"),
                Documento.DeEmpresa(propiaEliminada.Id, deEmpresa.Id, _hoy.AddMonths(-1), VigenciaDocumento.NoCaduca, "y/anterior.pdf"),
            };
            contexto.Documentos.AddRange(documentos);
            await contexto.SaveChangesAsync();
            documentos[5].MarcarComoEliminado(_usuarioContratista);
            eliminado.MarcarComoEliminado(_usuarioContratista);
            // El de Ana de hace 14 meses quedó en el historial: el vigente (documentos[1]) ocupó su lugar.
            documentos[7].SustituirPor(documentos[1], MotivoSustitucionDocumento.Renovacion, DateTime.UtcNow);
            documentos[8].Anonimizar(DateTime.UtcNow);
            propiaEliminada.MarcarComoEliminado(_usuarioContratista);

            AutorizacionLecturaDocumental Conceder(
                ContratoAcceso contrato, Guid? destinatario = null, DateOnly? desde = null, DateOnly? hasta = null, bool incluyeEmpresa = true)
            {
                var a = AutorizacionLecturaDocumental.Conceder(
                    destinatario ?? _lector.Id, contrato.Id, incluyeEmpresa, desde ?? _hoy.AddDays(-10), hasta, _usuarioContratista, DateTime.UtcNow);
                contexto.AutorizacionesLecturaDocumental.Add(a);
                return a;
            }

            var vigente = Conceder(_vigente);
            Conceder(_sinVinculo);
            var revocada = Conceder(_revocada);
            Conceder(_caducada, hasta: _hoy.AddDays(-1));
            var otroDestinatario = Conceder(_otroDestinatario, destinatario: _tercero);
            foreach (var contrato in _bordes.Where(c => c != _vigente))
                Conceder(contrato);
            var sinEmpresa = Conceder(_sinEmpresa, incluyeEmpresa: false);
            _vigenciasAutorizacion.AddRange([
                (_autHastaHoy, Conceder(_autHastaHoy, hasta: _hoy)),
                (_autDesdeHoy, Conceder(_autDesdeHoy, desde: _hoy)),
                (_autDesdeManana, Conceder(_autDesdeManana, desde: _hoy.AddDays(1))),
            ]);
            await contexto.SaveChangesAsync();

            // Los mismos Trabajadores en cada autorización: solo la mitad que falta explica que no se lea.
            foreach (var a in new[] { vigente, revocada, otroDestinatario })
                contexto.AutorizacionesLecturaTrabajadores.AddRange(a.AsignarTrabajador(asignado.Id), a.AsignarTrabajador(eliminado.Id));
            contexto.AutorizacionesLecturaTrabajadores.Add(vigente.AsignarTrabajador(anonimizado.Id));
            foreach (var a in _vigenciasAutorizacion.Select(v => v.Autorizacion).Append(sinEmpresa))
                contexto.AutorizacionesLecturaTrabajadores.Add(a.AsignarTrabajador(asignado.Id));
            await contexto.SaveChangesAsync();
            revocada.Revocar(_usuarioContratista, DateTime.UtcNow);
            anonimizado.Anonimizar(DateTime.UtcNow);
            await contexto.SaveChangesAsync();

            _autorizacion = vigente.Id;
            _deEmpresa = documentos[0].Id; _deTrabajador = documentos[1].Id; _deTrabajadorFuera = documentos[2].Id;
            _deTrabajadorEliminado = documentos[3].Id; _salud = documentos[4].Id; _eliminadoDoc = documentos[5].Id;
            _deContraparte = documentos[6].Id; _sustituido = documentos[7].Id; _anonimizadoDoc = documentos[8].Id;
            _deTrabajadorAnonimizado = documentos[9].Id; _deEmpresaPropiaEliminada = documentos[10].Id;
        }
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadena);

    [Fact]
    public async Task Con_contrato_vigente_vinculado_y_autorizacion_vigente_devuelve_exactamente_el_alcance()
    {
        await using var contexto = Contexto(_lector.Id, _usuarioLector);
        var lector = new LectorDocumentosInboundEnBase(contexto);

        var documentos = await lector.DocumentosAutorizadosAsync(_vigente.Id);

        documentos.Select(d => d.DocumentoId).Should().BeEquivalentTo([_deEmpresa, _deTrabajador],
            "los de la Empresa propia (IncluyeDocumentosEmpresa) y los del Trabajador asignado, y nada más");
        var deAna = documentos.Single(d => d.DocumentoId == _deTrabajador);
        (deAna.TrabajadorNombre, deAna.TrabajadorApellidos, deAna.TrabajadorDni, deAna.TipoDocumentoNombre)
            .Should().Be(("Ana", "Pérez", "12345678Z", "Formación PRL"));
        deAna.EstadoVigencia.Should().Be(EstadoVigenciaDocumento.VenceEnFecha);
        deAna.FechaVencimiento.Should().Be(_hoy.AddYears(1));
        documentos.Single(d => d.DocumentoId == _deEmpresa).TrabajadorId.Should().BeNull();

        (await lector.ArchivoAutorizadoAsync(_vigente.Id, _deTrabajador))
            .Should().Be(new ArchivoInboundDto(_deTrabajador, "y/ana.pdf"));
        (await lector.ArchivoAutorizadoAsync(_vigente.Id, _deContraparte))
            .Should().BeNull("un Documento de una Empresa contraparte de Y no es de su Empresa propia");
        (await lector.ArchivoAutorizadoAsync(_vigente.Id, _eliminadoDoc)).Should().BeNull("un Documento eliminado no se abre");

        var autorizacion = await lector.AutorizacionDelContratoAsync(_vigente.Id);
        autorizacion.Should().Be(new AutorizacionInboundDto(_autorizacion, _hoy.AddDays(-10), null, false, VigenteHoy: true, true, 1),
            "de los tres Trabajadores asignados solo Ana se puede leer: Eva está eliminada e Iker anonimizado");
    }

    [Fact]
    public async Task Un_tipo_de_categoria_especial_de_salud_nunca_aparece()
    {
        await using var contexto = Contexto(_lector.Id, _usuarioLector);
        var lector = new LectorDocumentosInboundEnBase(contexto);

        (await lector.DocumentosAutorizadosAsync(_vigente.Id)).Select(d => d.DocumentoId)
            .Should().NotContain(_salud, "el reconocimiento médico del Trabajador asignado es CategoriaEspecialSalud");
        (await lector.ArchivoAutorizadoAsync(_vigente.Id, _salud))
            .Should().BeNull("ni siquiera pidiéndolo por su identificador");
    }

    [Fact]
    public async Task Un_Documento_de_un_Trabajador_fuera_de_la_autorizacion_o_eliminado_no_aparece()
    {
        await using var contexto = Contexto(_lector.Id, _usuarioLector);
        var lector = new LectorDocumentosInboundEnBase(contexto);

        var ids = (await lector.DocumentosAutorizadosAsync(_vigente.Id)).Select(d => d.DocumentoId).ToList();
        ids.Should().NotContain(_deTrabajadorFuera, "su Trabajador no está entre los asignados a la autorización");
        ids.Should().NotContain(_deTrabajadorEliminado, "su Trabajador está asignado pero eliminado, y la FK de la fila hija no lo ve");
        (await lector.ArchivoAutorizadoAsync(_vigente.Id, _deTrabajadorFuera)).Should().BeNull();
        (await lector.ArchivoAutorizadoAsync(_vigente.Id, _deTrabajadorEliminado)).Should().BeNull();
    }

    [Fact]
    public async Task Una_autorizacion_revocada_no_da_lectura()
    {
        await using var contexto = Contexto(_lector.Id, _usuarioLector);
        var lector = new LectorDocumentosInboundEnBase(contexto);

        (await lector.DocumentosAutorizadosAsync(_revocada.Id)).Should().BeEmpty("la revocación corta la lectura desde ese instante");
        (await lector.ArchivoAutorizadoAsync(_revocada.Id, _deTrabajador)).Should().BeNull("la revocación corta también la apertura");
        // La autorización sigue visible como revocada: el Tenant del Cliente empresarial sabe por qué no puede validar.
        (await lector.AutorizacionDelContratoAsync(_revocada.Id))!.Revocada.Should().BeTrue();
        (await AccesosEnElTenantPropietarioAsync()).Should().BeEmpty("una lectura que no se dio no deja traza");
    }

    [Fact]
    public async Task Una_autorizacion_para_otro_Tenant_destinatario_no_da_lectura()
    {
        await using var contexto = Contexto(_lector.Id, _usuarioLector);
        var lector = new LectorDocumentosInboundEnBase(contexto);

        (await lector.DocumentosAutorizadosAsync(_otroDestinatario.Id))
            .Should().BeEmpty("Y autorizó a W para ese contrato, no a X, aunque el contrato de X esté vinculado con Y");
        (await lector.ArchivoAutorizadoAsync(_otroDestinatario.Id, _deTrabajador)).Should().BeNull();
        (await lector.AutorizacionDelContratoAsync(_otroDestinatario.Id)).Should().BeNull();
    }

    [Fact]
    public async Task Sin_vinculo_sin_autorizacion_caducada_o_con_el_contrato_cerrado_eliminado_o_futuro_no_hay_lectura()
    {
        await using var contexto = Contexto(_lector.Id, _usuarioLector);
        var lector = new LectorDocumentosInboundEnBase(contexto);

        foreach (var (contrato, motivo) in new[]
                 {
                     (_sinVinculo, "sin vínculo"), (_sinAutorizacion, "sin autorización"), (_caducada, "autorización caducada"),
                     (_cerrado, "contrato cerrado"), (_eliminado, "contrato eliminado"), (_futuro, "contrato futuro"),
                 })
        {
            (await lector.DocumentosAutorizadosAsync(contrato.Id)).Should().BeEmpty(motivo);
            (await lector.ArchivoAutorizadoAsync(contrato.Id, _deTrabajador)).Should().BeNull(motivo);
        }

        (await AccesosEnElTenantPropietarioAsync()).Should().BeEmpty("ninguna de esas llamadas dio lectura");
    }

    [Fact]
    public async Task Sin_contexto_firmado_las_tres_funciones_responden_42501()
    {
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadena), n => n.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .Options;
        await using var contexto = new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), new TenantActualAmbiental { TenantId = _lector.Id });
        (await contexto.Database.SqlQueryRaw<string>("SELECT current_user AS \"Value\"").SingleAsync()).Should().Be("cae_app_runtime");
        var lector = new LectorDocumentosInboundEnBase(contexto);

        foreach (var llamada in new Func<Task>[]
                 {
                     () => lector.AutorizacionDelContratoAsync(_vigente.Id),
                     () => lector.DocumentosAutorizadosAsync(_vigente.Id),
                     () => lector.ArchivoAutorizadoAsync(_vigente.Id, _deTrabajador),
                 })
        {
            (await llamada.Should().ThrowAsync<PostgresException>())
                .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege, "«sin contexto» no es «sin autorización»");
        }
    }

    [Fact]
    public async Task Con_el_token_de_otro_Tenant_no_hay_lectura_aunque_la_GUC_diga_el_Tenant_lector()
    {
        await using var contexto = Contexto(_contratista.Id, _usuarioContratista);
        var lector = new LectorDocumentosInboundEnBase(contexto);

        (await lector.DocumentosAutorizadosAsync(_vigente.Id)).Should().BeEmpty("el token es de Y y el contrato es de X");

        // La conexión puede escribir app.tenant_id, pero no firmar el contexto: el Tenant lector sale del token.
        await contexto.Database.OpenConnectionAsync();
        await contexto.Database.ExecuteSqlAsync($"SELECT set_config('app.tenant_id', {_lector.Id.ToString()}, false)");
        (await contexto.Database.SqlQueryRaw<string>("SELECT current_setting('app.tenant_id') AS \"Value\"").SingleAsync())
            .Should().Be(_lector.Id.ToString(), "control: la GUC quedó con el Tenant lector");

        (await lector.DocumentosAutorizadosAsync(_vigente.Id)).Should().BeEmpty("el Tenant lector sale del token firmado, no de la GUC ni de un parámetro");
        (await lector.ArchivoAutorizadoAsync(_vigente.Id, _deTrabajador)).Should().BeNull();
        (await lector.AutorizacionDelContratoAsync(_vigente.Id)).Should().BeNull();
    }

    [Fact]
    public async Task Cada_lectura_deja_su_fila_en_el_Tenant_propietario_y_el_runtime_no_puede_escribirla()
    {
        await using (var contexto = Contexto(_lector.Id, _usuarioLector))
        {
            var lector = new LectorDocumentosInboundEnBase(contexto);
            await lector.AutorizacionDelContratoAsync(_vigente.Id);
            await lector.DocumentosAutorizadosAsync(_vigente.Id);
            await lector.ArchivoAutorizadoAsync(_vigente.Id, _deTrabajador);
            await lector.ArchivoAutorizadoAsync(_vigente.Id, _salud);

            (await contexto.AccesosLecturaInbound.IgnoreQueryFilters().CountAsync())
                .Should().Be(0, "el registro vive en el Tenant propietario: RLS se lo oculta al lector");
        }

        var accesos = await AccesosEnElTenantPropietarioAsync();
        accesos.Select(a => (a.Tipo, a.DocumentoId)).Should().BeEquivalentTo(new (TipoAccesoLecturaInbound, Guid?)[]
        {
            (TipoAccesoLecturaInbound.Listado, null), (TipoAccesoLecturaInbound.Apertura, _deTrabajador),
        }, "consultar la autorización no lee Documentos, y una apertura denegada no deja fila");
        accesos.Should().AllSatisfy(a =>
        {
            a.TenantId.Should().Be(_contratista.Id);
            a.AutorizacionId.Should().Be(_autorizacion);
            a.ContratoAccesoId.Should().Be(_vigente.Id);
            a.TenantLectorId.Should().Be(_lector.Id);
            a.TenantOrigenLectorId.Should().BeNull("el usuario lector es del propio Tenant lector, no de un Operador CAE externo");
            a.UsuarioId.Should().Be(_usuarioLector);
        });

        await using var runtime = Contexto(_contratista.Id, _usuarioContratista);
        var unAcceso = accesos[0].Id;
        foreach (var (escribir, que) in new (Func<Task<int>>, string)[]
                 {
                     (() => runtime.Database.ExecuteSqlInterpolatedAsync($"""
                         INSERT INTO "AccesosLecturaInbound" ("Id", "TenantId", "AutorizacionId", "ContratoAccesoId", "TenantLectorId",
                             "UsuarioId", "Tipo", "MomentoUtc")
                         VALUES ({Guid.NewGuid()}, {_contratista.Id}, {_autorizacion}, {_vigente.Id}, {_lector.Id}, {_usuarioLector}, 'Listado', now());
                         """), "INSERT"),
                     (() => runtime.Database.ExecuteSqlAsync($"UPDATE \"AccesosLecturaInbound\" SET \"UsuarioId\" = {Guid.NewGuid()} WHERE \"Id\" = {unAcceso};"), "UPDATE"),
                     (() => runtime.Database.ExecuteSqlAsync($"DELETE FROM \"AccesosLecturaInbound\" WHERE \"Id\" = {unAcceso};"), "DELETE"),
                 })
        {
            (await escribir.Should().ThrowAsync<PostgresException>())
                .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege, $"cae_app_runtime no tiene {que} sobre el registro");
        }

        (await runtime.AccesosLecturaInbound.CountAsync()).Should().Be(2, "control positivo: el Tenant propietario lo lee bajo runtime");
    }

    [Fact]
    public async Task La_vigencia_del_contrato_en_SQL_coincide_con_VigenteEn_en_los_bordes()
    {
        await using var contexto = Contexto(_lector.Id, _usuarioLector);
        var lector = new LectorDocumentosInboundEnBase(contexto);

        _bordes.Select(c => c.VigenteEn(_hoy)).Should().Contain(true).And.Contain(false, "control: hay bordes de los dos lados");
        foreach (var contrato in _bordes)
        {
            var vigenteEnSql = await lector.AutorizacionDelContratoAsync(contrato.Id) is not null;
            vigenteEnSql.Should().Be(contrato.VigenteEn(_hoy),
                $"inicio {contrato.FechaInicio}, fin {contrato.FechaFin}, cierre {contrato.FechaCierreReal}, eliminado {contrato.EstaEliminado}, hoy {_hoy}");
        }
    }

    [Fact]
    public async Task Las_funciones_cumplen_su_contrato_de_privilegios()
    {
        await using var propietario = new NpgsqlConnection(_cadena);
        await propietario.OpenAsync();
        await using var orden = new NpgsqlCommand(
            """
            SELECT p.proname,
                   p.prosecdef,
                   pg_get_userbyid(p.proowner),
                   array_to_string(p.proconfig, ';'),
                   pg_get_function_identity_arguments(p.oid),
                   has_function_privilege('cae_app_runtime', p.oid, 'EXECUTE'),
                   has_function_privilege('cae_app_soporte', p.oid, 'EXECUTE'),
                   has_function_privilege('cae_app_aprovisionamiento', p.oid, 'EXECUTE'),
                   p.proacl IS NULL OR EXISTS (SELECT 1 FROM aclexplode(p.proacl) a WHERE a.grantee = 0),
                   p.provolatile
            FROM pg_proc p
            WHERE p.proname LIKE 'app\_inbound\_%' AND p.pronamespace = 'public'::regnamespace
            ORDER BY p.proname;
            """, propietario);
        var funciones = new Dictionary<string, (bool Definer, string Propietario, string Config, string Argumentos, bool Runtime, bool Soporte, bool Aprovisionamiento, bool Public, char Volatilidad)>();
        await using (var lector = await orden.ExecuteReaderAsync())
        {
            while (await lector.ReadAsync())
                funciones[lector.GetString(0)] = (lector.GetBoolean(1), lector.GetString(2), lector.GetString(3), lector.GetString(4),
                    lector.GetBoolean(5), lector.GetBoolean(6), lector.GetBoolean(7), lector.GetBoolean(8), lector.GetChar(9));
        }

        var publicas = new Dictionary<string, string>
        {
            ["app_inbound_autorizacion_del_contrato"] = "p_contrato uuid",
            ["app_inbound_documentos_autorizados"] = "p_contrato uuid",
            ["app_inbound_archivo_autorizado"] = "p_contrato uuid, p_documento uuid",
        };
        var auxiliares = new[]
        {
            "app_inbound_tenant_contratista_del_contrato", "app_inbound_autorizacion_aplicable", "app_inbound_documentos_del_alcance",
            "app_inbound_nuevo_id",
        };
        funciones.Keys.Should().BeEquivalentTo(publicas.Keys.Concat(auxiliares));

        foreach (var (nombre, argumentos) in publicas)
        {
            var f = funciones[nombre];
            f.Definer.Should().BeTrue($"{nombre}: SECURITY DEFINER");
            f.Propietario.Should().NotBe("cae_app_runtime");
            f.Config.Should().Be("search_path=pg_catalog, pg_temp", nombre);
            f.Argumentos.Should().Be(argumentos, $"{nombre}: ningún parámetro elige Tenant lector ni usuario");
            f.Runtime.Should().BeTrue(nombre);
            f.Soporte.Should().BeFalse(nombre);
            f.Aprovisionamiento.Should().BeFalse(nombre);
            f.Public.Should().BeFalse($"{nombre}: REVOKE ALL ... FROM PUBLIC");
            f.Volatilidad.Should().Be('v', $"{nombre}: no es STABLE, porque escribe la traza");
        }

        foreach (var nombre in auxiliares)
        {
            var f = funciones[nombre];
            f.Definer.Should().BeFalse($"{nombre}: corre como quien la llama, que solo puede ser una función pública ya elevada");
            f.Config.Should().Be("search_path=pg_catalog, pg_temp", nombre);
            (f.Runtime || f.Soporte || f.Aprovisionamiento || f.Public).Should().BeFalse($"{nombre}: solo la llama el propietario");
        }

        // El supuesto del que depende la migración: las tablas tienen FORCE ROW LEVEL SECURITY, así que un propietario
        // sin rolsuper ni rolbypassrls vería sus propias políticas y las funciones no leerían nada del Tenant de Y.
        await using var propietarios = new NpgsqlCommand(
            """
            SELECT r.rolname, r.rolsuper OR r.rolbypassrls
            FROM pg_proc p JOIN pg_roles r ON r.oid = p.proowner
            WHERE p.proname LIKE 'app\_inbound\_%' AND p.pronamespace = 'public'::regnamespace;
            """, propietario);
        var saltaRls = new List<(string Rol, bool SaltaRls)>();
        await using (var lector = await propietarios.ExecuteReaderAsync())
        {
            while (await lector.ReadAsync())
                saltaRls.Add((lector.GetString(0), lector.GetBoolean(1)));
        }

        saltaRls.Should().HaveCount(funciones.Count);
        saltaRls.Should().AllSatisfy(p => p.SaltaRls.Should().BeTrue(
            $"{p.Rol} es propietario de las funciones app_inbound_* y debe tener rolsuper o rolbypassrls (FORCE ROW LEVEL SECURITY)"));
    }

    [Fact]
    public async Task Un_Documento_sustituido_no_se_lista_ni_se_abre_y_el_vigente_si()
    {
        await using var contexto = Contexto(_lector.Id, _usuarioLector);
        var lector = new LectorDocumentosInboundEnBase(contexto);

        var ids = (await lector.DocumentosAutorizadosAsync(_vigente.Id)).Select(d => d.DocumentoId).ToList();
        ids.Should().Contain(_deTrabajador, "control: el vigente de Ana, que ocupó el lugar del anterior, sí se lee");
        ids.Should().NotContain(_sustituido, "el de hace 14 meses está en el historial (SustituidoEnUtc), como en DocumentoOperativo");
        (await lector.ArchivoAutorizadoAsync(_vigente.Id, _sustituido)).Should().BeNull("ni siquiera pidiéndolo por su identificador");
        (await lector.ArchivoAutorizadoAsync(_vigente.Id, _deTrabajador)).Should().NotBeNull("control positivo de la apertura");
    }

    [Fact]
    public async Task Sin_IncluyeDocumentosEmpresa_no_salen_los_de_la_Empresa_propia_y_si_los_de_sus_Trabajadores()
    {
        await using var contexto = Contexto(_lector.Id, _usuarioLector);
        var lector = new LectorDocumentosInboundEnBase(contexto);

        (await lector.DocumentosAutorizadosAsync(_sinEmpresa.Id)).Select(d => d.DocumentoId)
            .Should().BeEquivalentTo([_deTrabajador], "la autorización no incluye los de Empresa, solo los de Ana, su hija");
        (await lector.ArchivoAutorizadoAsync(_sinEmpresa.Id, _deEmpresa)).Should().BeNull("tampoco se abre el de la Empresa propia");
        (await lector.ArchivoAutorizadoAsync(_sinEmpresa.Id, _deTrabajador)).Should().NotBeNull();
        (await lector.AutorizacionDelContratoAsync(_sinEmpresa.Id))!.IncluyeDocumentosEmpresa.Should().BeFalse();
    }

    [Fact]
    public async Task Un_Documento_anonimizado_de_un_Trabajador_anonimizado_o_de_una_Empresa_propia_eliminada_no_aparece()
    {
        await using var contexto = Contexto(_lector.Id, _usuarioLector);
        var lector = new LectorDocumentosInboundEnBase(contexto);

        var ids = (await lector.DocumentosAutorizadosAsync(_vigente.Id)).Select(d => d.DocumentoId).ToList();
        foreach (var (documento, motivo) in new[]
                 {
                     (_anonimizadoDoc, "Documento anonimizado"), (_deTrabajadorAnonimizado, "Trabajador asignado pero anonimizado"),
                     (_deEmpresaPropiaEliminada, "Empresa propia eliminada"),
                 })
        {
            ids.Should().NotContain(documento, motivo);
            (await lector.ArchivoAutorizadoAsync(_vigente.Id, documento)).Should().BeNull(motivo);
        }
    }

    [Fact]
    public async Task Los_bordes_de_la_vigencia_de_la_autorizacion_coinciden_con_VigenteEn()
    {
        await using var contexto = Contexto(_lector.Id, _usuarioLector);
        var lector = new LectorDocumentosInboundEnBase(contexto);

        _vigenciasAutorizacion.Select(v => v.Autorizacion.VigenteEn(_hoy)).Should().Equal(
            [true, true, false], "control: VigenciaHasta = hoy y VigenciaDesde = hoy son vigentes; VigenciaDesde = mañana no");
        foreach (var (contrato, autorizacion) in _vigenciasAutorizacion)
        {
            var bordes = $"desde {autorizacion.VigenciaDesde}, hasta {autorizacion.VigenciaHasta}, hoy {_hoy}";
            var vigente = autorizacion.VigenteEn(_hoy);
            (await lector.DocumentosAutorizadosAsync(contrato.Id)).Any().Should().Be(vigente, $"listado: {bordes}");
            (await lector.ArchivoAutorizadoAsync(contrato.Id, _deTrabajador) is not null).Should().Be(vigente, $"apertura: {bordes}");
            (await lector.AutorizacionDelContratoAsync(contrato.Id))!.VigenteHoy.Should().Be(vigente, $"VigenteHoy: {bordes}");
        }
    }

    [Fact]
    public async Task Con_un_usuario_de_un_Operador_CAE_externo_la_traza_guarda_su_Tenant_de_origen()
    {
        var operadorExterno = Guid.NewGuid();
        await using (var contexto = Contexto(_lector.Id, usuarioActual: new CurrentUserServiceFalso(_usuarioLector, Roles.GestorCae, tenantOrigenId: operadorExterno)))
        {
            var lector = new LectorDocumentosInboundEnBase(contexto);
            (await lector.DocumentosAutorizadosAsync(_vigente.Id)).Should().NotBeEmpty("control: la lectura se dio");
            await lector.ArchivoAutorizadoAsync(_vigente.Id, _deTrabajador);
        }

        var accesos = await AccesosEnElTenantPropietarioAsync();
        accesos.Should().HaveCount(2);
        accesos.Should().AllSatisfy(a =>
        {
            a.TenantLectorId.Should().Be(_lector.Id, "el Tenant beneficiario sobre el que opera");
            a.TenantOrigenLectorId.Should().Be(operadorExterno, "el Tenant del Operador CAE externo al que pertenece el usuario");
            a.UsuarioId.Should().Be(_usuarioLector);
        });
    }

    [Fact]
    public async Task Con_un_contexto_firmado_sin_usuario_las_tres_funciones_responden_42501()
    {
        await using var contexto = Contexto(_lector.Id, usuarioActual: new CurrentUserServiceFalso(null, Roles.Administrador, tenantOrigenId: _lector.Id));
        (await contexto.Database.SqlQueryRaw<bool>("SELECT COALESCE(app_ctx_valido(), false) AND app_ctx_tenant_id() IS NOT NULL AS \"Value\"").SingleAsync())
            .Should().BeTrue("control: el contexto está firmado y lleva Tenant; solo falta el usuario");
        var lector = new LectorDocumentosInboundEnBase(contexto);

        foreach (var llamada in new Func<Task>[]
                 {
                     () => lector.AutorizacionDelContratoAsync(_vigente.Id),
                     () => lector.DocumentosAutorizadosAsync(_vigente.Id),
                     () => lector.ArchivoAutorizadoAsync(_vigente.Id, _deTrabajador),
                 })
        {
            (await llamada.Should().ThrowAsync<PostgresException>())
                .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege, "sin usuario no hay a quién atribuir la lectura");
        }

        (await AccesosEnElTenantPropietarioAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Un_tercer_Tenant_vinculado_por_su_cuenta_con_la_Empresa_contratista_no_lee_nada()
    {
        await using var contexto = Contexto(_otroLector.Id, _usuarioOtroLector);
        var lector = new LectorDocumentosInboundEnBase(contexto);

        foreach (var (contrato, motivo) in new[]
                 {
                     (_deOtroLector.Id, "su propio contrato, vigente y vinculado con Y, pero Y no autorizó nada a Z"),
                     (_vigente.Id, "el contrato de X, que no es de Z"),
                 })
        {
            (await lector.AutorizacionDelContratoAsync(contrato)).Should().BeNull(motivo);
            (await lector.DocumentosAutorizadosAsync(contrato)).Should().BeEmpty(motivo);
            (await lector.ArchivoAutorizadoAsync(contrato, _deTrabajador)).Should().BeNull(motivo);
        }

        (await AccesosEnElTenantPropietarioAsync()).Should().BeEmpty("ninguna de esas llamadas dio lectura");
    }

    [Fact]
    public async Task Dentro_de_una_transaccion_las_lecturas_con_traza_se_rechazan_antes_de_llamar_a_la_base()
    {
        await using (var contexto = Contexto(_lector.Id, _usuarioLector))
        {
            var lector = new LectorDocumentosInboundEnBase(contexto);
            await using (var transaccion = await contexto.Database.BeginTransactionAsync())
            {
                (await lector.AutorizacionDelContratoAsync(_vigente.Id)).Should().NotBeNull("control: la consulta sin traza sí se permite");
                await FluentActions.Awaiting(() => lector.DocumentosAutorizadosAsync(_vigente.Id))
                    .Should().ThrowAsync<InvalidOperationException>().WithMessage("*transacción*");
                await FluentActions.Awaiting(() => lector.ArchivoAutorizadoAsync(_vigente.Id, _deTrabajador))
                    .Should().ThrowAsync<InvalidOperationException>().WithMessage("*transacción*");
                await transaccion.RollbackAsync();
            }

            using (new System.Transactions.TransactionScope(System.Transactions.TransactionScopeAsyncFlowOption.Enabled))
            {
                await FluentActions.Awaiting(() => lector.DocumentosAutorizadosAsync(_vigente.Id))
                    .Should().ThrowAsync<InvalidOperationException>("también una transacción ambiental, en la que Npgsql se alista");
            }

            (await lector.DocumentosAutorizadosAsync(_vigente.Id)).Should().NotBeEmpty("control: fuera de la transacción se lee");
        }

        (await AccesosEnElTenantPropietarioAsync()).Select(a => a.Tipo)
            .Should().Equal([TipoAccesoLecturaInbound.Listado], "solo la lectura de fuera de la transacción; las rechazadas no llegaron a la base");
    }

    private async Task<List<AccesoLecturaInbound>> AccesosEnElTenantPropietarioAsync()
    {
        // Identidad propietaria (no sujeta a RLS) y filtro de EF del Tenant de la Empresa contratista.
        await using var contexto = Contexto(_contratista.Id);
        return await contexto.AccesosLecturaInbound.OrderBy(a => a.Tipo).ToListAsync();
    }

    private static void Cuenta(CaeManagerDbContext contexto, Guid id, Guid tenantId, Guid rolId)
    {
        contexto.Users.Add(new ApplicationUser
        {
            Id = id,
            TenantId = tenantId,
            UserName = $"{id:N}@caemanager.local",
            NormalizedUserName = $"{id:N}@CAEMANAGER.LOCAL",
            Email = $"{id:N}@caemanager.local",
            NormalizedEmail = $"{id:N}@CAEMANAGER.LOCAL",
            NombreCompleto = "Usuario",
            EmailConfirmed = true,
            SecurityStamp = Guid.NewGuid().ToString(),
            ConcurrencyStamp = Guid.NewGuid().ToString()
        });
        contexto.UserRoles.Add(new IdentityUserRole<Guid> { UserId = id, RoleId = rolId });
    }

    // Sin usuario ni usuarioActual: identidad propietaria (siembra y lectura de control). Con cualquiera de los dos:
    // cae_app_runtime con contexto firmado.
    private CaeManagerDbContext Contexto(Guid tenantId, Guid? usuario = null, ICurrentUserService? usuarioActual = null)
    {
        usuarioActual ??= usuario is { } id ? new CurrentUserServiceFalso(id, Roles.Administrador, tenantOrigenId: tenantId) : null;
        var tenant = new TenantActualAmbiental { TenantId = tenantId };
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>().UseNpgsql(
            usuarioActual is null ? _cadena : BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadena),
            n => n.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"));
        opciones.AddInterceptors(new TenantSelladoInterceptor(tenant));
        if (usuarioActual is not null)
            opciones.AddInterceptors(new TenantRlsConnectionInterceptor(tenant, new SinTenantSeleccionado(),
                usuarioActual, BaseDatosPostgresDePruebas.FirmanteContextoRls),
                new ConcurrenciaOptimistaInterceptor());
        return new(opciones.Options, new EphemeralDataProtectionProvider(), tenant);
    }

    private sealed class SinTenantSeleccionado : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }
}
