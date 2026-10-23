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
/// y de las autorizaciones); W, un tercer Tenant. Lo que prueba la base: solo las dos mitades del apretón juntas dan
/// lectura, y solo del alcance autorizado; nunca un tipo de categoría especial de salud; el Tenant lector sale del token,
/// no de la GUC; cada lectura deja su fila en Y y el runtime no la escribe; y la vigencia del contrato en SQL es la de
/// <see cref="ContratoAcceso.VigenteEn"/>.
/// </summary>
public class LecturaInboundBajoRuntimeTests : IAsyncLifetime
{
    private readonly string _cadena = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Tenant _lector = new("Tenant del Cliente empresarial");
    private readonly Tenant _contratista = new("Tenant de la Empresa contratista");
    private readonly Guid _tercero = Guid.NewGuid();
    private readonly Guid _usuarioLector = Guid.NewGuid();
    private readonly Guid _usuarioContratista = Guid.NewGuid();
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

    // Documentos de Y.
    private Guid _autorizacion;
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
            contexto.Tenants.AddRange(_lector, _contratista);
            var roles = await contexto.Roles.ToDictionaryAsync(r => r.Name!, r => r.Id);
            Cuenta(contexto, _usuarioLector, _lector.Id, roles[Roles.GestorCae]);
            Cuenta(contexto, _usuarioContratista, _contratista.Id, roles[Roles.Administrador]);

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
            await contexto.SaveChangesAsync();
        }

        await using (var contexto = Contexto(_contratista.Id))
        {
            var propia = new Empresa("Empresa contratista", "B10380194");
            var contraparte = Empresa.CrearComoCliente("Empresa contraparte de la contratista", "B10380202", false, null, null);
            contexto.Empresas.AddRange(propia, contraparte);
            var general = new TipoDocumento("Formación PRL", null, false, 1, AmbitoAplicacion.Trabajador, sensibilidad: SensibilidadDocumental.DatosPersonales);
            var deEmpresa = new TipoDocumento("Seguro de responsabilidad civil", null, false, 2, AmbitoAplicacion.Empresa, sensibilidad: SensibilidadDocumental.SinDatosPersonales);
            var salud = new TipoDocumento("Reconocimiento médico", null, false, 3, AmbitoAplicacion.Trabajador);
            contexto.TiposDocumento.AddRange(general, deEmpresa, salud);
            await contexto.SaveChangesAsync();

            var asignado = Trabajador.DeEmpresa(propia.Id, "Ana", "Pérez", "12345678Z");
            var fuera = Trabajador.DeEmpresa(propia.Id, "Luis", "García", "00000000T");
            var eliminado = Trabajador.DeEmpresa(propia.Id, "Eva", "Ruiz", "00000001R");
            contexto.Trabajadores.AddRange(asignado, fuera, eliminado);
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
            };
            contexto.Documentos.AddRange(documentos);
            await contexto.SaveChangesAsync();
            documentos[5].MarcarComoEliminado(_usuarioContratista);
            eliminado.MarcarComoEliminado(_usuarioContratista);

            AutorizacionLecturaDocumental Conceder(ContratoAcceso contrato, Guid? destinatario = null, DateOnly? hasta = null)
            {
                var a = AutorizacionLecturaDocumental.Conceder(
                    destinatario ?? _lector.Id, contrato.Id, true, _hoy.AddDays(-10), hasta, _usuarioContratista, DateTime.UtcNow);
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
            await contexto.SaveChangesAsync();

            // Los mismos Trabajadores en cada autorización: solo la mitad que falta explica que no se lea.
            foreach (var a in new[] { vigente, revocada, otroDestinatario })
                contexto.AutorizacionesLecturaTrabajadores.AddRange(a.AsignarTrabajador(asignado.Id), a.AsignarTrabajador(eliminado.Id));
            await contexto.SaveChangesAsync();
            revocada.Revocar(_usuarioContratista, DateTime.UtcNow);
            await contexto.SaveChangesAsync();

            _autorizacion = vigente.Id;
            _deEmpresa = documentos[0].Id; _deTrabajador = documentos[1].Id; _deTrabajadorFuera = documentos[2].Id;
            _deTrabajadorEliminado = documentos[3].Id; _salud = documentos[4].Id; _eliminadoDoc = documentos[5].Id;
            _deContraparte = documentos[6].Id;
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
        autorizacion.Should().Be(new AutorizacionInboundDto(_autorizacion, _hoy.AddDays(-10), null, false, true, 2));
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

    private CaeManagerDbContext Contexto(Guid tenantId, Guid? usuario = null)
    {
        var tenant = new TenantActualAmbiental { TenantId = tenantId };
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>().UseNpgsql(
            usuario is null ? _cadena : BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadena),
            n => n.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"));
        opciones.AddInterceptors(new TenantSelladoInterceptor(tenant));
        if (usuario is not null)
            opciones.AddInterceptors(new TenantRlsConnectionInterceptor(tenant, new SinTenantSeleccionado(),
                new CurrentUserServiceFalso(usuario.Value, Roles.Administrador, tenantOrigenId: tenantId), BaseDatosPostgresDePruebas.FirmanteContextoRls),
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
