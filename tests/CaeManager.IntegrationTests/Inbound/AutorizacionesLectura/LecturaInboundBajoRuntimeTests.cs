using System.Text.RegularExpressions;
using CaeManager.Application.Common;
using CaeManager.Application.Inbound.ContratosAcceso.LecturaInbound;
using CaeManager.Domain.AltaAutonoma;
using CaeManager.Domain.Inbound.AutorizacionesLectura;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Comercial;
using CaeManager.Domain.Common;
using CaeManager.Domain.Inbound.ContratosAcceso;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Tenants;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Inbound.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Migrations.PostgreSQL.Migrations;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Inbound.AutorizacionesLectura;

/// <summary>
/// Lectura entre Tenants de Inbound M1 (PR 3) bajo <c>cae_app_runtime</c> con contexto firmado, por
/// <see cref="LectorDocumentosInboundEnBase"/> y las funciones <c>app_inbound_*</c>. X es el Tenant del Cliente empresarial
/// (lector, dueño de los contratos de acceso); Y, el Tenant de la Empresa contratista (Tenant propietario de los Documentos
/// y de las autorizaciones); W, un destinatario que no es X; Z, otro Tenant de Cliente empresarial que vincula por su
/// cuenta un contrato suyo con Y. Lo que prueba la base: sin la línea Inbound vigente en el Producto Contratado de X
/// (DN-7) no hay lectura; solo las dos mitades del apretón juntas dan lectura, y solo del
/// alcance autorizado (sin sustituidos, anonimizados ni eliminados, y sin los de Empresa si la autorización no los
/// incluye o si Y no tiene exactamente una Empresa propia no eliminada); nunca un tipo de categoría especial de salud; el Tenant lector y el usuario salen del token, no de la GUC;
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

    // Producto Contratado de la línea Inbound de X (DN-7) y el Pagador TALVEG que lo paga.
    private const string PagadorDelLector = "cus_PagadorDelLector";
    private Guid _lineaInbound;

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
    private Guid _empresaPropia;
    private Guid _empresaPropiaEliminada;
    private Guid _tipoDeEmpresa;

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

            ContratoAcceso Contrato(DateOnly inicio, DateOnly? fin = null, bool vincular = true, bool pendiente = false)
            {
                var c = ContratoAcceso.Crear(cliente.Id, centro.Id, contratistaLocal.Id, inicio, fin, null, pendienteDeConfirmar: pendiente);
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
            _cerrado.Finalizar(_hoy.AddDays(-1));
            _eliminado = Contrato(_hoy.AddDays(-10));
            _eliminado.MarcarComoEliminado(_usuarioLector);
            _futuro = Contrato(_hoy.AddDays(1));
            var cierreHoy = Contrato(_hoy.AddDays(-10));
            cierreHoy.Finalizar(_hoy);
            // B-05: en fechas, los dos darían acceso; el estado lo quita (X-1 para el pendiente, B-18 para el suspendido).
            var suspendido = Contrato(_hoy.AddDays(-10));
            suspendido.Suspender();
            var pendiente = Contrato(_hoy.AddDays(-10), pendiente: true);
            _bordes.AddRange([
                _vigente, _cerrado, _eliminado, _futuro, cierreHoy, suspendido, pendiente,
                Contrato(_hoy), Contrato(_hoy.AddDays(-10), _hoy), Contrato(_hoy.AddDays(-10), _hoy.AddDays(-1))]);
            _sinEmpresa = Contrato(_hoy.AddDays(-10));
            _autHastaHoy = Contrato(_hoy.AddDays(-10));
            _autDesdeHoy = Contrato(_hoy.AddDays(-10));
            _autDesdeManana = Contrato(_hoy.AddDays(-10));

            // DN-7: sin la línea Inbound vigente en X, ninguna función app_inbound_* le responde.
            var lineaInbound = LineaInboundVigente();
            contexto.ProductosContratados.Add(lineaInbound);
            _lineaInbound = lineaInbound.Id;
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
            // Z también tiene la línea Inbound: si no lee, es por el apretón, no por el gate DN-7.
            contexto.ProductosContratados.Add(LineaInboundVigente());
            await contexto.SaveChangesAsync();
        }

        await using (var contexto = Contexto(_contratista.Id))
        {
            var propia = new Empresa("Empresa contratista", "B10380194");
            var contraparte = Empresa.CrearComoCliente("Empresa contraparte de la contratista", "B10380202", false, null, null);
            var propiaEliminada = new Empresa("Empresa contratista anterior", "B10380228");
            contexto.Empresas.AddRange(propia, contraparte, propiaEliminada);
            // Y también tiene la línea Inbound: con el token de Y no lee el contrato de X por el apretón, no por el gate.
            contexto.ProductosContratados.Add(LineaInboundVigente());
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
            _empresaPropia = propia.Id; _empresaPropiaEliminada = propiaEliminada.Id; _tipoDeEmpresa = deEmpresa.Id;
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
            .Should().Be(new ArchivoInboundDto(_deTrabajador, _contratista.Id, "y/ana.pdf", false));
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
                $"inicio {contrato.FechaInicio}, fin {contrato.FechaFin}, cierre {contrato.FechaCierreReal}, eliminado {contrato.EstaEliminado}, estado {contrato.Estado}, hoy {_hoy}");
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
        // sin rolsuper ni rolbypassrls vería sus propias políticas y no leería nada del Tenant de Y. Las tres funciones
        // públicas fallan cerradas en ese caso (FalloCerradoLecturaInbound), en vez de confundirlo con «sin autorización».
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
    public async Task La_migracion_de_fallo_cerrado_solo_anade_la_guardia_y_su_Down_solo_la_quita()
    {
        // El SQL de la migración sobre el catálogo ya migrado, dentro de una transacción: no depende de las migraciones
        // que lleguen después ni de que su Down funcione, y aun así reescribe los cuerpos reales, con su contexto firmado.
        var migracion = new FalloCerradoLecturaInbound();
        await using var conexion = new NpgsqlConnection(_cadena);
        await conexion.OpenAsync();
        await using var transaccion = await conexion.BeginTransactionAsync();

        var protegidas = await FuncionesInboundAsync(conexion, transaccion);
        await EjecutarAsync(conexion, transaccion, migracion.DownOperations.OfType<SqlOperation>().Single().Sql);
        var anteriores = await FuncionesInboundAsync(conexion, transaccion);
        await EjecutarAsync(conexion, transaccion, migracion.UpOperations.OfType<SqlOperation>().Single().Sql);

        (await FuncionesInboundAsync(conexion, transaccion)).Should().Equal(protegidas);
        foreach (var (nombre, anterior) in anteriores)
        {
            var protegida = protegidas[nombre];
            anterior.PropietarioYPermisos.Should().Be(protegida.PropietarioYPermisos, nombre);
            anterior.Definicion.Should().NotContain("no evita la RLS", nombre);
            anterior.Definicion.Should().Contain("app_ctx_valido()", $"{nombre}: conserva la comprobación del contexto firmado");

            var inicio = protegida.Definicion.IndexOf("BEGIN", StringComparison.Ordinal) + "BEGIN".Length;
            protegida.Definicion[inicio..].TrimStart().Should().StartWith("IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles",
                $"{nombre}: la guardia va antes que cualquier lectura");
            var fin = protegida.Definicion.IndexOf("END IF;", inicio, StringComparison.Ordinal) + "END IF;".Length;
            protegida.Definicion.Remove(inicio, fin - inicio).Should().Be(anterior.Definicion, $"{nombre}: nada más cambia");
        }

        await transaccion.RollbackAsync();
    }

    [Theory]
    [InlineData("app_inbound_autorizacion_del_contrato(uuid)", "SELECT * FROM public.app_inbound_autorizacion_del_contrato(NULL)")]
    [InlineData("app_inbound_documentos_autorizados(uuid)", "SELECT * FROM public.app_inbound_documentos_autorizados(NULL)")]
    [InlineData("app_inbound_archivo_autorizado(uuid,uuid)", "SELECT * FROM public.app_inbound_archivo_autorizado(NULL, NULL)")]
    public async Task Sin_propietario_que_evita_rls_la_lectura_falla_cerrada(string firma, string consulta)
    {
        await using var conexion = new NpgsqlConnection(_cadena);
        await conexion.OpenAsync();
        await using var transaccion = await conexion.BeginTransactionAsync();
        await EjecutarAsync(conexion, transaccion, $"ALTER FUNCTION public.{firma} OWNER TO cae_app_runtime");

        await using var leer = new NpgsqlCommand(consulta, conexion, transaccion);
        var excepcion = await Assert.ThrowsAsync<PostgresException>(() => leer.ExecuteNonQueryAsync());

        excepcion.MessageText.Should().Contain("no evita la RLS").And.Contain("Tenant propietario");
        await transaccion.RollbackAsync();
    }

    private sealed record FuncionInbound(string Definicion, string PropietarioYPermisos);

    private static async Task<Dictionary<string, FuncionInbound>> FuncionesInboundAsync(NpgsqlConnection conexion, NpgsqlTransaction transaccion)
    {
        await using var comando = new NpgsqlCommand(
            """
            SELECT proname, pg_get_functiondef(oid), pg_get_userbyid(proowner) || ' ' || COALESCE(proacl::text, '')
            FROM pg_proc
            WHERE oid IN ('public.app_inbound_autorizacion_del_contrato(uuid)'::regprocedure,
                          'public.app_inbound_documentos_autorizados(uuid)'::regprocedure,
                          'public.app_inbound_archivo_autorizado(uuid,uuid)'::regprocedure)
            """, conexion, transaccion);
        var funciones = new Dictionary<string, FuncionInbound>();
        await using var filas = await comando.ExecuteReaderAsync();
        while (await filas.ReadAsync())
            funciones.Add(filas.GetString(0), new FuncionInbound(filas.GetString(1), filas.GetString(2)));
        funciones.Should().HaveCount(3);
        return funciones;
    }

    private static async Task EjecutarAsync(NpgsqlConnection conexion, NpgsqlTransaction transaccion, string sql)
    {
        await using var comando = new NpgsqlCommand(sql, conexion, transaccion);
        await comando.ExecuteNonQueryAsync();
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

    // ---------- EP-6, opción (d), 2026-11-12: Documentos de Empresa solo con una única Empresa propia no eliminada ----------
    // La autorización no dice qué Empresa propia de Y expone. Con una sola no hay duda; con dos o más (ampliación de grupo)
    // la base no devuelve ninguna, en vez de dejar leer la de una sociedad hermana que no es parte del contrato.

    [Fact]
    public async Task Con_una_unica_Empresa_propia_en_el_Tenant_contratista_se_leen_sus_Documentos_de_Empresa()
    {
        // Control positivo: la Empresa propia eliminada del fixture deja de ser propia, así que Y tiene exactamente una.
        await EmpresaDejaDeSerPropiaAsync(_empresaPropiaEliminada);
        (await EmpresasPropiasDelContratistaAsync()).Should().Equal([(_empresaPropia, false)],
            "precondición: Y tiene una sola Empresa propia, y no está eliminada");

        await using var contexto = Contexto(_lector.Id, _usuarioLector);
        var lector = new LectorDocumentosInboundEnBase(contexto);

        (await lector.DocumentosAutorizadosAsync(_vigente.Id)).Select(d => d.DocumentoId)
            .Should().BeEquivalentTo([_deEmpresa, _deTrabajador], "el de su única Empresa propia y el del Trabajador asignado");
        (await lector.ArchivoAutorizadoAsync(_vigente.Id, _deEmpresa))
            .Should().Be(new ArchivoInboundDto(_deEmpresa, _contratista.Id, "y/empresa.pdf", false));
    }

    [Fact]
    public async Task Con_dos_Empresas_propias_no_eliminadas_no_se_lee_ningun_Documento_de_Empresa_y_si_los_de_Trabajador()
    {
        var deLaHermana = await AnadirEmpresaPropiaConDocumentoAsync();
        (await EmpresasPropiasDelContratistaAsync()).Count(e => !e.Eliminada).Should().Be(2,
            "precondición: Y tiene dos Empresas propias no eliminadas, la del fixture y la sociedad hermana");

        await using var contexto = Contexto(_lector.Id, _usuarioLector);
        var lector = new LectorDocumentosInboundEnBase(contexto);

        (await lector.DocumentosAutorizadosAsync(_vigente.Id)).Select(d => d.DocumentoId)
            .Should().BeEquivalentTo([_deTrabajador],
                "la autorización no dice cuál de las dos expone: ninguna (falla cerrado), y el Trabajador asignado sigue");
        (await lector.ArchivoAutorizadoAsync(_vigente.Id, _deEmpresa)).Should().BeNull("tampoco se abre el de la primera Empresa propia");
        (await lector.ArchivoAutorizadoAsync(_vigente.Id, deLaHermana)).Should().BeNull("ni el de la sociedad hermana");
        (await lector.ArchivoAutorizadoAsync(_vigente.Id, _deTrabajador)).Should().NotBeNull("control: el del Trabajador asignado se abre");
        (await lector.AutorizacionDelContratoAsync(_vigente.Id))!.IncluyeDocumentosEmpresa
            .Should().BeTrue("la autorización no cambia: es la base la que no encuentra una única Empresa propia");
    }

    [Fact]
    public async Task Con_dos_Empresas_propias_y_una_eliminada_cuenta_una_y_se_leen_los_de_la_activa()
    {
        // El fixture tal cual: la Empresa propia y la anterior, eliminada.
        (await EmpresasPropiasDelContratistaAsync()).Should().Equal([(_empresaPropia, false), (_empresaPropiaEliminada, true)],
            "precondición: dos Empresas propias en Y, una de ellas eliminada");

        await using var contexto = Contexto(_lector.Id, _usuarioLector);
        var lector = new LectorDocumentosInboundEnBase(contexto);

        (await lector.DocumentosAutorizadosAsync(_vigente.Id)).Select(d => d.DocumentoId)
            .Should().BeEquivalentTo([_deEmpresa, _deTrabajador], "la eliminada no cuenta: queda una y se leen los suyos");
        (await lector.ArchivoAutorizadoAsync(_vigente.Id, _deEmpresa)).Should().NotBeNull("se abre el de la activa");
        (await lector.ArchivoAutorizadoAsync(_vigente.Id, _deEmpresaPropiaEliminada)).Should().BeNull("y no el de la eliminada");
    }

    [Fact]
    public async Task La_migracion_de_Empresa_propia_unica_solo_anade_el_recuento_y_su_Down_solo_lo_quita()
    {
        // Como la de fallo cerrado: el SQL de la migración sobre el catálogo ya migrado, dentro de una transacción.
        var migracion = new LecturaInboundEmpresaPropiaUnica();
        await using var conexion = new NpgsqlConnection(_cadena);
        await conexion.OpenAsync();
        await using var transaccion = await conexion.BeginTransactionAsync();

        var vigente = await DelAlcanceAsync(conexion, transaccion);
        await EjecutarAsync(conexion, transaccion, migracion.DownOperations.OfType<SqlOperation>().Single().Sql);
        var anterior = await DelAlcanceAsync(conexion, transaccion);
        await EjecutarAsync(conexion, transaccion, migracion.UpOperations.OfType<SqlOperation>().Single().Sql);

        (await DelAlcanceAsync(conexion, transaccion)).Should().Be(vigente, "el Up deja la función como la deja la migración");
        anterior.PropietarioYPermisos.Should().Be(vigente.PropietarioYPermisos, "CREATE OR REPLACE conserva propietario y privilegios");
        RecuentoDeEmpresasPropias.Matches(vigente.Definicion).Should().HaveCount(1, "la rama de Empresa exige una única Empresa propia");
        anterior.Definicion.Should().NotContain("count(*)", "el Down vuelve a la versión de AnadeEvidenciasInbound");
        RecuentoDeEmpresasPropias.Replace(vigente.Definicion, "").Should().Be(anterior.Definicion,
            "fuera del recuento, el cuerpo, la firma, el lenguaje, la volatilidad y el search_path son los mismos");

        await transaccion.RollbackAsync();
    }

    private static readonly Regex RecuentoDeEmpresasPropias = new(
        @"\s+AND \(SELECT count\(\*\) FROM public\.""Empresas"" e2\s+WHERE e2\.""TenantId"" = p_tenant AND e2\.""EsPropia"" AND NOT e2\.""EstaEliminado""\) = 1",
        RegexOptions.CultureInvariant);

    private static async Task<FuncionInbound> DelAlcanceAsync(NpgsqlConnection conexion, NpgsqlTransaction transaccion)
    {
        await using var comando = new NpgsqlCommand(
            """
            SELECT pg_get_functiondef(oid), pg_get_userbyid(proowner) || ' ' || COALESCE(proacl::text, '')
            FROM pg_proc
            WHERE oid = 'public.app_inbound_documentos_del_alcance(uuid,uuid,boolean)'::regprocedure
            """, conexion, transaccion);
        await using var fila = await comando.ExecuteReaderAsync();
        (await fila.ReadAsync()).Should().BeTrue();
        return new FuncionInbound(fila.GetString(0), fila.GetString(1));
    }

    private async Task<List<(Guid Id, bool Eliminada)>> EmpresasPropiasDelContratistaAsync()
    {
        // Identidad propietaria: ve también las eliminadas, que el filtro de EF ocultaría.
        await using var conexion = new NpgsqlConnection(_cadena);
        await conexion.OpenAsync();
        await using var comando = new NpgsqlCommand(
            """
            SELECT "Id", "EstaEliminado" FROM public."Empresas"
             WHERE "TenantId" = @tenant AND "EsPropia"
             ORDER BY "EstaEliminado", "Id"
            """, conexion);
        comando.Parameters.AddWithValue("tenant", _contratista.Id);
        var empresas = new List<(Guid Id, bool Eliminada)>();
        await using var filas = await comando.ExecuteReaderAsync();
        while (await filas.ReadAsync())
            empresas.Add((filas.GetGuid(0), filas.GetBoolean(1)));
        return empresas;
    }

    private async Task EmpresaDejaDeSerPropiaAsync(Guid empresa)
    {
        // El dominio no tiene la operación inversa de EsPropia; para el control positivo basta con la columna.
        await using var conexion = new NpgsqlConnection(_cadena);
        await conexion.OpenAsync();
        await using var comando = new NpgsqlCommand(
            """UPDATE public."Empresas" SET "EsPropia" = false WHERE "TenantId" = @tenant AND "Id" = @empresa""", conexion);
        comando.Parameters.AddWithValue("tenant", _contratista.Id);
        comando.Parameters.AddWithValue("empresa", empresa);
        (await comando.ExecuteNonQueryAsync()).Should().Be(1);
    }

    /// <summary>Una segunda Empresa propia no eliminada en Y (la ampliación de grupo de D-P11), con un Documento de Empresa.</summary>
    private async Task<Guid> AnadirEmpresaPropiaConDocumentoAsync()
    {
        await using var contexto = Contexto(_contratista.Id);
        var hermana = new Empresa("Sociedad hermana de la contratista", "B10380236");
        contexto.Empresas.Add(hermana);
        await contexto.SaveChangesAsync();
        var documento = Documento.DeEmpresa(hermana.Id, _tipoDeEmpresa, _hoy.AddMonths(-1), VigenciaDocumento.NoCaduca, "y/hermana.pdf");
        contexto.Documentos.Add(documento);
        await contexto.SaveChangesAsync();
        return documento.Id;
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

    // ---------- DN-7, opción (a), y D-1 (2026-11-14): la línea Inbound «a presupuesto» en el Producto Contratado ----------
    // La lectura entre Tenants exige que el Tenant del Cliente empresarial (X, Tenant beneficiario) tenga un Producto
    // Contratado de la línea Inbound vigente. Lo decide el TenantId de la fila, nunca el Pagador TALVEG. El fixture se lo da
    // a X, a Y y a Z (así los demás negativos fallan solo por su motivo): cada prueba de abajo se lo quita a X de una forma
    // y comprueba 0 filas en las tres funciones públicas, sin traza.
    // Mutación declarada: quitar el EXISTS de app_inbound_tenant_contratista_del_contrato deja estas pruebas en rojo.

    [Fact]
    public async Task Sin_linea_Inbound_vigente_en_el_Tenant_lector_las_tres_funciones_devuelven_0_filas()
    {
        await using (var control = Contexto(_lector.Id, _usuarioLector))
        {
            (await new LectorDocumentosInboundEnBase(control).AutorizacionDelContratoAsync(_vigente.Id))
                .Should().NotBeNull("control: con la línea Inbound vigente el mismo contrato sí responde");
        }

        await FinalizarLineaInboundDelLectorAsync();

        await ComprobarSinLecturaAsync(_lector.Id, _usuarioLector, "la línea Inbound de X está finalizada");
    }

    [Fact]
    public async Task Una_linea_Inbound_eliminada_o_solo_la_linea_Outbound_no_abren_la_lectura()
    {
        await using (var contexto = Contexto(_lector.Id))
        {
            var linea = await contexto.ProductosContratados.SingleAsync(p => p.Id == _lineaInbound);
            linea.MarcarComoEliminado(_usuarioLector);
            contexto.ProductosContratados.Add(ProductoContratado.Contratar(LineaProductoContratado.OutboundGestionPropia, PagadorDelLector,
                TramoTrabajadoresActivos.De1A10, ModalidadSuscripcionAlta.Anual, DateTime.UtcNow.AddDays(-30), DateTime.UtcNow));
            await contexto.SaveChangesAsync();
        }

        await ComprobarSinLecturaAsync(_lector.Id, _usuarioLector,
            "la línea Inbound está eliminada, aunque siga Vigente, y la Outbound vigente no es la línea Inbound");
    }

    [Fact]
    public async Task Una_linea_Inbound_vigente_con_inicio_futuro_no_abre_la_lectura()
    {
        // El dominio no deja contratar con inicio futuro (ProductoContratado.Contratar), pero la base sí lo admitiría: el
        // gate sigue el criterio de EstaVigenteEn y no da por vigente lo que aún no ha empezado.
        await FinalizarLineaInboundDelLectorAsync();
        await using (var conexion = new NpgsqlConnection(_cadena))
        {
            await conexion.OpenAsync();
            await using var insertar = new NpgsqlCommand(
                """
                INSERT INTO "ProductosContratados" ("Id", "Linea", "PagadorStripeCustomerId", "Tramo", "Modalidad",
                    "VigenteDesdeUtc", "VigenteHastaUtc", "Estado", "TenantId", "Version", "CreadoEnUtc", "EstaEliminado")
                VALUES (@id, 'Inbound', 'cus_PagadorDelLector', 'De1A10', 'Anual', now() + interval '1 day', NULL, 'Vigente',
                    @tenant, @id, now(), false);
                """, conexion);
            insertar.Parameters.AddWithValue("id", Guid.NewGuid());
            insertar.Parameters.AddWithValue("tenant", _lector.Id);
            (await insertar.ExecuteNonQueryAsync()).Should().Be(1, "precondición: la fila existe, vigente y con inicio mañana");
        }

        await ComprobarSinLecturaAsync(_lector.Id, _usuarioLector, "la línea Inbound de X empieza mañana");
    }

    [Fact]
    public async Task La_linea_Inbound_de_otro_Tenant_o_del_mismo_Pagador_no_abre_la_lectura_del_Tenant_lector()
    {
        await FinalizarLineaInboundDelLectorAsync();
        foreach (var tenant in new[] { _contratista.Id, _otroLector.Id })
        {
            await using var contexto = Contexto(tenant);
            (await contexto.ProductosContratados.CountAsync(p => p.Linea == LineaProductoContratado.Inbound
                    && p.Estado == EstadoProductoContratado.Vigente && p.PagadorStripeCustomerId == PagadorDelLector))
                .Should().Be(1, "precondición: el fixture da a Y y a Z su línea Inbound vigente, con el Pagador TALVEG de X");
        }

        await ComprobarSinLecturaAsync(_lector.Id, _usuarioLector,
            "Y y Z tienen la línea Inbound vigente, pagada por el mismo Pagador TALVEG que X, pero el gate mira el Tenant beneficiario lector");
    }

    [Fact]
    public async Task La_migracion_de_la_linea_Inbound_solo_anade_el_gate_y_su_Down_no_reabre_la_lectura()
    {
        // Como las de fallo cerrado y Empresa propia única: el SQL de la migración sobre el catálogo ya migrado, en una
        // transacción. Los CHECK de la columna los cubren ProductosContratadosBajoRuntimeTests y el modelo de EF.
        // El Down no restaura la versión de AnadeLecturaInboundEntreTenants: eso reabriría la lectura entre Tenants a un
        // Tenant sin línea Inbound. Deja la auxiliar denegando siempre, también con contrato, autorización y línea vigentes.
        // Mutación declarada: quitar el AND false del Down deja esta prueba en rojo.
        var migracion = new AnadeLineaInboundProductoContratado();
        await using var conexion = new NpgsqlConnection(_cadena);
        await conexion.OpenAsync();
        await using var transaccion = await conexion.BeginTransactionAsync();

        // La auxiliar vigente ya no es la de esta migración: AnadeEstadoContratoAcceso (B-05) le añadió el filtro de estado,
        // y su propia prueba comprueba que solo añade eso. Aquí se compara el cuerpo que deja el Up de B-01.
        (await EvaluarTenantContratistaAsync(conexion, transaccion)).Should().Be(_contratista.Id,
            "control: con la migración aplicada, el contrato vigente del fixture, con la línea Inbound de X, responde");

        await EjecutarAsync(conexion, transaccion, migracion.DownOperations.OfType<SqlOperation>().Single().Sql);
        var revertida = await TenantContratistaDelContratoAsync(conexion, transaccion);
        (await EvaluarTenantContratistaAsync(conexion, transaccion)).Should().BeNull(
            "tras el Down la auxiliar deniega siempre: revertir no reabre la lectura entre Tenants");

        await EjecutarAsync(conexion, transaccion, migracion.UpOperations.OfType<SqlOperation>().Single().Sql);
        var vigente = await TenantContratistaDelContratoAsync(conexion, transaccion);
        (await EvaluarTenantContratistaAsync(conexion, transaccion)).Should().Be(_contratista.Id, "el Up vuelve a abrirla con la línea");

        revertida.PropietarioYPermisos.Should().Be(vigente.PropietarioYPermisos, "CREATE OR REPLACE conserva propietario y privilegios");
        GateLineaInbound.Matches(vigente.Definicion).Should().HaveCount(1, "la función exige la línea Inbound vigente del Tenant lector");
        DenegacionTotal.Matches(revertida.Definicion).Should().HaveCount(1, "el Down deniega del todo");
        revertida.Definicion.Should().NotContain("ProductosContratados",
            "el cuerpo denegador no depende de la tabla, que el Down de AnadeProductosContratados puede borrar después");
        GateLineaInbound.Replace(vigente.Definicion, "").Should().Be(DenegacionTotal.Replace(revertida.Definicion, ""),
            "fuera del gate y de la denegación, el cuerpo, la firma, el lenguaje, la volatilidad y el search_path son los mismos");

        await transaccion.RollbackAsync();
    }

    [Fact]
    public async Task La_migracion_de_estado_del_contrato_solo_anade_el_filtro_de_estado_y_su_Down_solo_lo_quita()
    {
        // B-05: como las anteriores, el SQL de la migración sobre el catálogo ya migrado, en una transacción.
        var migracion = new AnadeEstadoContratoAcceso();
        static string DeLaAuxiliar(IEnumerable<MigrationOperation> operaciones) => operaciones.OfType<SqlOperation>()
            .Single(o => o.Sql.Contains("app_inbound_tenant_contratista_del_contrato", StringComparison.Ordinal)).Sql;
        await using var conexion = new NpgsqlConnection(_cadena);
        await conexion.OpenAsync();
        await using var transaccion = await conexion.BeginTransactionAsync();

        // La auxiliar vigente ya no es la de esta migración: GateInboundConRelojDeEvaluacion le cambió el reloj del gate, y
        // su propia prueba comprueba que solo cambia eso. Aquí se parte del cuerpo que deja el Up de B-05.
        await EjecutarAsync(conexion, transaccion, DeLaAuxiliar(migracion.UpOperations));
        var vigente = await TenantContratistaDelContratoAsync(conexion, transaccion);
        await EjecutarAsync(conexion, transaccion, DeLaAuxiliar(migracion.DownOperations));
        var anterior = await TenantContratistaDelContratoAsync(conexion, transaccion);
        await EjecutarAsync(conexion, transaccion, DeLaAuxiliar(migracion.UpOperations));

        (await TenantContratistaDelContratoAsync(conexion, transaccion)).Should().Be(vigente, "el Up deja la función como la deja la migración");
        anterior.PropietarioYPermisos.Should().Be(vigente.PropietarioYPermisos, "CREATE OR REPLACE conserva propietario y privilegios");
        FiltroEstadoContrato.Matches(vigente.Definicion).Should().HaveCount(1, "solo Activo y Finalizado dan acceso");
        GateLineaInbound.Matches(anterior.Definicion).Should().HaveCount(1, "el Down vuelve a la versión de B-01, con el gate");
        FiltroEstadoContrato.Replace(vigente.Definicion, "").Should().Be(anterior.Definicion,
            "fuera del filtro de estado, el cuerpo, la firma, el lenguaje, la volatilidad y el search_path son los mismos");

        await transaccion.RollbackAsync();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Una_transaccion_lectora_anterior_a_la_correccion_ve_la_corregida_y_tras_una_baja_ninguna(bool corregir)
    {
        // Issue #2257 y hallazgo 1 de Codex, ronda 3, en la #2256: la función real del gate, no una copia de su predicado.
        // La lectora (READ COMMITTED) fija su now() antes de que empiece la escritora y consulta después de su COMMIT. La
        // escritora hace lo que CorregirLineaInboundCommand y DarDeBajaLineaInboundCommand: fin de la vigente y, al
        // corregir, inicio de la nueva en el now() de su propia transacción. Con el now() de la lectora en el gate, la
        // corregida no contaría (su inicio es posterior) y esta prueba fallaría.
        await using var lectora = new NpgsqlConnection(_cadena);
        await lectora.OpenAsync();
        await using var lectura = await lectora.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
        await using (var fijar = new NpgsqlCommand("SELECT now(); SELECT pg_sleep(0.05);", lectora, lectura))
            await fijar.ExecuteNonQueryAsync();
        (await EvaluarTenantContratistaAsync(lectora, lectura)).Should().Be(_contratista.Id, "control: antes, la línea vigente cuenta");

        await CorregirODarDeBajaConfirmadoAsync(corregir);

        await using (var vigentes = new NpgsqlCommand(
                         $"""
                         SELECT count(*) FROM public."ProductosContratados"
                          WHERE "TenantId" = '{_lector.Id}' AND "Linea" = 'Inbound' AND "Estado" = 'Vigente' AND NOT "EstaEliminado"
                            AND "VigenteDesdeUtc" > now();
                         """, lectora, lectura))
            ((long)(await vigentes.ExecuteScalarAsync())!).Should().Be(corregir ? 1 : 0,
                "control: la lectora ve confirmada la corregida, con un inicio posterior a su now()");
        (await EvaluarTenantContratistaAsync(lectora, lectura)).Should().Be(corregir ? _contratista.Id : null,
            corregir
                ? "sin hueco: la lectora anterior ve exactamente una línea vigente, la corregida"
                : "tras la baja confirmada, ninguna línea vigente cuenta");

        await lectura.RollbackAsync();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Una_lectura_publica_cuya_consulta_interna_se_evalua_tras_la_correccion_sigue_autorizada_y_tras_una_baja_no(bool corregir)
    {
        // Hallazgo 1 de Codex, ronda 4, en la #2256: la función pública real, bajo cae_app_runtime con contexto firmado.
        // Una sentencia exterior (el DO) empieza, se retiene en un candado consultivo, y mientras tanto la escritora corrige
        // o da de baja y confirma. Solo entonces la consulta interna llama a app_inbound_documentos_autorizados, que es
        // PL/pgSQL VOLATILE: su instantánea ve el COMMIT, pero statement_timestamp() sigue siendo el inicio del DO, anterior
        // al inicio de la corregida. Con statement_timestamp() (o now()) en el gate, la corrección dejaría la lectura vacía.
        // Sin tiempos: la escritora solo empieza cuando la lectora ya espera el candado (pg_locks).
        var clave = Random.Shared.NextInt64(1, long.MaxValue);
        await using var contexto = Contexto(_lector.Id, _usuarioLector);
        await contexto.Database.OpenConnectionAsync();
        var lectora = (NpgsqlConnection)contexto.Database.GetDbConnection();
        var documentosAntes = await DocumentosAutorizadosEnSesionAsync(lectora);
        documentosAntes.Should().BePositive("control: antes, el contrato vigente con la línea Inbound da lectura");
        int pidLectora;
        await using (var pid = new NpgsqlCommand("SELECT pg_backend_pid()", lectora))
            pidLectora = (int)(await pid.ExecuteScalarAsync())!;

        await using var bloqueo = new NpgsqlConnection(_cadena);
        await bloqueo.OpenAsync();
        await EjecutarSinTransaccionAsync(bloqueo, $"SELECT pg_advisory_lock({clave})");

        var lectura = EjecutarSinTransaccionAsync(lectora,
            $"""
            DO $$
            BEGIN
              PERFORM pg_catalog.pg_advisory_lock({clave});
              PERFORM pg_catalog.set_config('prueba.inicio_sentencia', pg_catalog.statement_timestamp()::text, false);
              PERFORM pg_catalog.set_config('prueba.documentos',
                  (SELECT count(*)::text FROM public.app_inbound_documentos_autorizados('{_vigente.Id}'::uuid)), false);
              PERFORM pg_catalog.pg_advisory_unlock({clave});
            END
            $$;
            """);
        await EsperarAQueEspereElCandadoAsync(bloqueo, pidLectora, lectura);

        var nueva = await CorregirODarDeBajaConfirmadoAsync(corregir);
        await EjecutarSinTransaccionAsync(bloqueo, $"SELECT pg_advisory_unlock({clave})");
        await lectura;

        if (nueva is { } id)
            (await EmpiezaDespuesDeAsync(id, await LeerAjusteAsync(lectora, "prueba.inicio_sentencia"))).Should().BeTrue(
                "control: la corregida empieza después del statement_timestamp() de la sentencia exterior");
        long.Parse(await LeerAjusteAsync(lectora, "prueba.documentos"), System.Globalization.CultureInfo.InvariantCulture)
            .Should().Be(corregir ? documentosAntes : 0,
                corregir
                    ? "sin hueco: la consulta interna que ve la corrección confirmada cuenta la corregida"
                    : "tras la baja confirmada, ninguna línea vigente da lectura");
    }

    [Fact]
    public async Task Una_instantanea_que_aun_no_ve_la_correccion_sigue_viendo_la_sustituida_vigente_y_no_la_nueva()
    {
        // Caso inverso (contrato de la coordinadora, ronda 5): clock_timestamp() no concede nada que la instantánea no vea.
        // La lectora en REPEATABLE READ fija su instantánea antes de la corrección; tras el COMMIT, la hora ya pasó el inicio
        // de la corregida, pero para esa instantánea la corrección no existe. Cuenta la sustituida porque la ve Vigente, y
        // no la nueva porque no está confirmada para ella: la razón es la visibilidad, no la hora.
        await using var lectora = new NpgsqlConnection(_cadena);
        await lectora.OpenAsync();
        await using var lectura = await lectora.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);
        (await EvaluarTenantContratistaAsync(lectora, lectura)).Should().Be(_contratista.Id, "control: la instantánea queda fijada aquí");

        var nueva = await CorregirODarDeBajaConfirmadoAsync(corregir: true);

        (await LineasInboundVigentesAsync(lectora, lectura)).Should().Equal(new[] { _lineaInbound },
            "la instantánea anterior ve la sustituida Vigente y no ve la corregida");
        (await EvaluarTenantContratistaAsync(lectora, lectura)).Should().Be(_contratista.Id,
            "cuenta la sustituida, que para esta instantánea sigue vigente");
        await lectura.RollbackAsync();

        await using var despues = new NpgsqlConnection(_cadena);
        await despues.OpenAsync();
        await using var nuevaLectura = await despues.BeginTransactionAsync();
        (await LineasInboundVigentesAsync(despues, nuevaLectura)).Should().Equal(new[] { nueva!.Value },
            "control: una instantánea posterior al COMMIT solo ve la corregida");
        await nuevaLectura.RollbackAsync();
    }

    /// <summary>
    /// Lo que hacen <c>CorregirLineaInboundCommand</c> y <c>DarDeBajaLineaInboundCommand</c>, en una transacción propia
    /// confirmada: fin de la línea vigente del lector y, al corregir, la nueva con el inicio en el <c>now()</c> de esa
    /// transacción. Devuelve el Id de la nueva, o null tras una baja.
    /// </summary>
    private async Task<Guid?> CorregirODarDeBajaConfirmadoAsync(bool corregir)
    {
        Guid? nueva = corregir ? Guid.NewGuid() : null;
        await using var escritora = new NpgsqlConnection(_cadena);
        await escritora.OpenAsync();
        await using var escritura = await escritora.BeginTransactionAsync();
        await EjecutarAsync(escritora, escritura,
            $"""UPDATE public."ProductosContratados" SET "Estado" = 'Finalizado', "VigenteHastaUtc" = now() WHERE "Id" = '{_lineaInbound}';""");
        if (nueva is { } id)
            await EjecutarAsync(escritora, escritura,
                $"""
                INSERT INTO public."ProductosContratados" ("Id", "Linea", "PagadorStripeCustomerId", "Tramo", "Modalidad",
                    "VigenteDesdeUtc", "VigenteHastaUtc", "Estado", "TenantId", "Version", "CreadoEnUtc", "EstaEliminado")
                VALUES ('{id}', 'Inbound', '{PagadorDelLector}', 'De11A40', 'Anual', now(), NULL, 'Vigente',
                    '{_lector.Id}', '{Guid.NewGuid()}', now(), false);
                """);
        await escritura.CommitAsync();
        return nueva;
    }

    private async Task<List<Guid>> LineasInboundVigentesAsync(NpgsqlConnection conexion, NpgsqlTransaction transaccion)
    {
        await using var comando = new NpgsqlCommand(
            $"""
            SELECT "Id" FROM public."ProductosContratados"
             WHERE "TenantId" = '{_lector.Id}' AND "Linea" = 'Inbound' AND "Estado" = 'Vigente' AND NOT "EstaEliminado"
            """, conexion, transaccion);
        var ids = new List<Guid>();
        await using var filas = await comando.ExecuteReaderAsync();
        while (await filas.ReadAsync())
            ids.Add(filas.GetGuid(0));
        return ids;
    }

    private async Task<bool> EmpiezaDespuesDeAsync(Guid producto, string instante)
    {
        // La comparación la hace la base, con los dos instantes en timestamptz: sin conversiones de texto en .NET.
        await using var conexion = new NpgsqlConnection(_cadena);
        await conexion.OpenAsync();
        await using var comando = new NpgsqlCommand(
            """SELECT "VigenteDesdeUtc" > @instante::timestamptz FROM public."ProductosContratados" WHERE "Id" = @producto""", conexion);
        comando.Parameters.AddWithValue("instante", instante);
        comando.Parameters.AddWithValue("producto", producto);
        return (bool)(await comando.ExecuteScalarAsync())!;
    }

    private async Task<long> DocumentosAutorizadosEnSesionAsync(NpgsqlConnection conexion)
    {
        await using var comando = new NpgsqlCommand(
            $"SELECT count(*) FROM public.app_inbound_documentos_autorizados('{_vigente.Id}'::uuid)", conexion);
        return (long)(await comando.ExecuteScalarAsync())!;
    }

    private static async Task EjecutarSinTransaccionAsync(NpgsqlConnection conexion, string sql)
    {
        await using var comando = new NpgsqlCommand(sql, conexion);
        await comando.ExecuteNonQueryAsync();
    }

    private static async Task<string> LeerAjusteAsync(NpgsqlConnection conexion, string nombre)
    {
        await using var comando = new NpgsqlCommand($"SELECT current_setting('{nombre}')", conexion);
        return (string)(await comando.ExecuteScalarAsync())!;
    }

    private static async Task EsperarAQueEspereElCandadoAsync(NpgsqlConnection observadora, int pid, Task lectura)
    {
        // Sondeo del catálogo, no un tiempo fijo: termina en cuanto la lectora espera el candado consultivo.
        var limite = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            if (lectura.IsCompleted)
            {
                await lectura; // propaga el error de la lectora, si lo hubo
                throw new InvalidOperationException("La lectora terminó sin quedar retenida en el candado consultivo.");
            }
            await using (var espera = new NpgsqlCommand(
                             $"SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND NOT granted AND pid = {pid}", observadora))
                if ((long)(await espera.ExecuteScalarAsync())! == 1)
                    return;
            DateTime.UtcNow.Should().BeBefore(limite, "la lectora no llegó a esperar el candado");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task La_migracion_del_reloj_de_evaluacion_solo_cambia_el_reloj_del_gate_y_su_Down_lo_restaura()
    {
        // Opción B del 2026-11-15 (hallazgo 1 de Codex, rondas 3 y 4, en la #2256): el gate compara con clock_timestamp().
        var migracion = new GateInboundConRelojDeEvaluacion();
        await using var conexion = new NpgsqlConnection(_cadena);
        await conexion.OpenAsync();
        await using var transaccion = await conexion.BeginTransactionAsync();

        var vigente = await TenantContratistaDelContratoAsync(conexion, transaccion);
        (await EvaluarTenantContratistaAsync(conexion, transaccion)).Should().Be(_contratista.Id,
            "control: con la migración aplicada, el contrato vigente del fixture, con la línea Inbound de X, responde");
        await EjecutarAsync(conexion, transaccion, migracion.DownOperations.OfType<SqlOperation>().Single().Sql);
        var anterior = await TenantContratistaDelContratoAsync(conexion, transaccion);
        await EjecutarAsync(conexion, transaccion, migracion.UpOperations.OfType<SqlOperation>().Single().Sql);

        (await TenantContratistaDelContratoAsync(conexion, transaccion)).Should().Be(vigente, "el Up deja la función como la deja la migración");
        anterior.PropietarioYPermisos.Should().Be(vigente.PropietarioYPermisos, "CREATE OR REPLACE conserva propietario y privilegios");
        GateLineaInbound.Matches(anterior.Definicion).Should().HaveCount(1, "el Down vuelve al gate con el now() de B-05");
        GateLineaInbound.Matches(vigente.Definicion).Should().BeEmpty("el gate vigente ya no compara con now()");
        vigente.Definicion.Should().Contain("""pc."VigenteDesdeUtc" <= pg_catalog.clock_timestamp());""");
        vigente.Definicion.Should().Contain(" STABLE", "la función del gate sigue siendo STABLE: usa la instantánea de quien la llama");
        vigente.Definicion.Replace("pg_catalog.clock_timestamp()", "pg_catalog.now()").Should().Be(anterior.Definicion,
            "fuera del reloj del gate, el cuerpo, la firma, el lenguaje, la volatilidad y el search_path son los mismos");

        await transaccion.RollbackAsync();
    }

    [Theory]
    [InlineData("Activo", null, true)]
    [InlineData("Activo", 0, true)]          // cerrado hoy por la ranura anterior, que no toca la columna Estado
    [InlineData("Finalizado", 0, true)]
    [InlineData("Finalizado", 10, true)]
    [InlineData("PendienteDeConfirmar", null, false)]
    [InlineData("Suspendido", null, false)]
    [InlineData("Suspendido", 0, false)]     // un cierre real de hoy o futuro no le devuelve el acceso a un Suspendido
    [InlineData("Suspendido", 10, false)]
    [InlineData("Cancelado", null, false)]
    [InlineData("Cancelado", 10, false)]
    [InlineData("Rechazado", null, false)]
    public async Task Solo_un_contrato_Activo_o_Finalizado_da_lectura_entre_Tenants(string estado, int? diasHastaElCierre, bool daLectura)
    {
        // Con la identidad propietaria y en una transacción: el mismo contrato vigente en fechas, vinculado y con la línea
        // Inbound de X, en cada estado, sin cierre real o con uno de hoy o futuro (Finalizado lo exige:
        // CK_ContratosAcceso_FinalizadoConCierre).
        await using var conexion = new NpgsqlConnection(_cadena);
        await conexion.OpenAsync();
        await using var transaccion = await conexion.BeginTransactionAsync();
        await FijarEstadoYCierreAsync(conexion, transaccion, _vigente.Id, estado,
            diasHastaElCierre is { } dias ? _hoy.AddDays(dias) : null);

        (await EvaluarTenantContratistaAsync(conexion, transaccion)).Should().Be(daLectura ? _contratista.Id : null,
            $"{estado} con cierre {diasHastaElCierre?.ToString() ?? "ninguno"}");

        await transaccion.RollbackAsync();
    }

    [Theory]
    [InlineData("Activo", -10, null, true)]
    [InlineData("Activo", 0, null, true)]
    [InlineData("Finalizado", -10, 0, true)]
    [InlineData("Suspendido", -10, null, false)]
    [InlineData("Suspendido", -10, 10, false)]        // cierre futuro que escribió la ranura anterior
    [InlineData("Suspendido", 0, null, false)]        // empieza hoy: el cierre en el inicio aún daría hoy
    [InlineData("Suspendido", -1, null, false)]       // empezó ayer
    [InlineData("Suspendido", -400, null, false)]     // antiguo: también eliminado, sin margen de fechas
    [InlineData("Cancelado", -400, 10, false)]
    [InlineData("PendienteDeConfirmar", -10, null, false)]
    [InlineData("PendienteDeConfirmar", 0, null, false)]
    [InlineData("Cancelado", -10, null, false)]
    [InlineData("Cancelado", 0, 10, false)]
    [InlineData("Rechazado", -10, null, false)]
    [InlineData("Rechazado", 0, null, false)]
    public async Task El_Down_de_la_migracion_de_estado_no_devuelve_la_lectura_a_un_contrato_que_no_daba_acceso(
        string estado, int diasDesdeElInicio, int? diasHastaElCierre, bool daLectura)
    {
        // El Down entero sobre el catálogo ya migrado, en una transacción: sus SQL en orden (disparador, relleno que falla
        // cerrado, auxiliar de B-01). Sin el relleno, la auxiliar de B-01 decide solo por fechas y todos los casos de abajo
        // darían lectura. Activo y Finalizado son el control: el Down no les quita lo que ya daban.
        // Mutación declarada: quitar el relleno, quitar la marca de eliminado o volver a acotarla por fecha de inicio deja
        // esta prueba en rojo (los casos de hoy, de ayer y antiguos se leen también en el día de su inicio).
        var migracion = new AnadeEstadoContratoAcceso();
        await using var conexion = new NpgsqlConnection(_cadena);
        await conexion.OpenAsync();
        await using var transaccion = await conexion.BeginTransactionAsync();
        await using (var inicio = new NpgsqlCommand(
                         """UPDATE public."ContratosAcceso" SET "FechaInicio" = @inicio WHERE "Id" = @contrato""", conexion, transaccion))
        {
            inicio.Parameters.AddWithValue("inicio", _hoy.AddDays(diasDesdeElInicio));
            inicio.Parameters.AddWithValue("contrato", _vigente.Id);
            (await inicio.ExecuteNonQueryAsync()).Should().Be(1);
        }
        await FijarEstadoYCierreAsync(conexion, transaccion, _vigente.Id, estado,
            diasHastaElCierre is { } dias ? _hoy.AddDays(dias) : null);

        (await EvaluarTenantContratistaAsync(conexion, transaccion)).Should().Be(daLectura ? _contratista.Id : null,
            "control: con la migración aplicada, solo Activo y Finalizado dan lectura");

        var sqlDelDown = migracion.DownOperations.OfType<SqlOperation>().Select(o => o.Sql).ToList();
        sqlDelDown.Should().HaveCount(3, "disparador, relleno y auxiliar, en ese orden");
        foreach (var sql in sqlDelDown)
            await EjecutarAsync(conexion, transaccion, sql);

        (await EvaluarTenantContratistaAsync(conexion, transaccion)).Should().Be(daLectura ? _contratista.Id : null,
            $"tras el Down, la auxiliar de B-01 no da lectura a un {estado} que no la daba");
        // El peor día de negocio para quien lea: el del inicio, que el cierre en el inicio aún cubriría (cierre inclusivo).
        // Es el caso de una transacción o una petición cuyo día de negocio quedó atrás: sin margen de fechas, la marca de
        // eliminado lo cierra sea cual sea el día.
        (await EvaluarTenantContratistaAsync(conexion, transaccion, _hoy.AddDays(diasDesdeElInicio))).Should().Be(
            daLectura ? _contratista.Id : null, $"tras el Down, tampoco el día de su inicio da lectura un {estado}");

        // Qué hizo el relleno: a todo lo que no es Activo ni Finalizado, sea cual sea su fecha, el cierre en el inicio y la
        // marca de eliminado. Activo y Finalizado, intactos.
        var sinAcceso = estado is not ("Activo" or "Finalizado");
        await using (var leer = new NpgsqlCommand(
                         """SELECT "FechaCierreReal", "EstaEliminado" FROM public."ContratosAcceso" WHERE "Id" = @contrato""",
                         conexion, transaccion))
        {
            leer.Parameters.AddWithValue("contrato", _vigente.Id);
            await using var fila = await leer.ExecuteReaderAsync();
            (await fila.ReadAsync()).Should().BeTrue();
            DateOnly? cierre = fila.IsDBNull(0) ? null : fila.GetFieldValue<DateOnly>(0);
            DateOnly? cierreEsperado = sinAcceso ? _hoy.AddDays(diasDesdeElInicio)
                : diasHastaElCierre is { } d ? _hoy.AddDays(d) : null;
            cierre.Should().Be(cierreEsperado);
            fila.GetBoolean(1).Should().Be(sinAcceso, "el Down marca como eliminado todo lo que no es Activo ni Finalizado");
        }

        await transaccion.RollbackAsync();
    }

    private static async Task FijarEstadoYCierreAsync(
        NpgsqlConnection conexion, NpgsqlTransaction transaccion, Guid contrato, string estado, DateOnly? cierre)
    {
        await using var cambiar = new NpgsqlCommand(
            """
            UPDATE public."ContratosAcceso" SET "Estado" = @estado, "FechaCierreReal" = @cierre WHERE "Id" = @contrato
            """, conexion, transaccion);
        cambiar.Parameters.AddWithValue("estado", estado);
        cambiar.Parameters.Add(new NpgsqlParameter("cierre", NpgsqlTypes.NpgsqlDbType.Date) { Value = cierre is { } c ? (object)c : DBNull.Value });
        cambiar.Parameters.AddWithValue("contrato", contrato);
        (await cambiar.ExecuteNonQueryAsync()).Should().Be(1);
    }

    private static readonly Regex FiltroEstadoContrato = new(
        @"\s+AND c\.""Estado"" IN \('Activo', 'Finalizado'\)", RegexOptions.CultureInvariant);

    private static readonly Regex GateLineaInbound = new(
        @"\s+AND EXISTS \(SELECT 1 FROM public\.""ProductosContratados"" pc\s+WHERE pc\.""TenantId"" = p_lector AND pc\.""Linea"" = 'Inbound'\s+AND pc\.""Estado"" = 'Vigente' AND NOT pc\.""EstaEliminado""\s+AND pc\.""VigenteDesdeUtc"" <= pg_catalog\.now\(\)\)",
        RegexOptions.CultureInvariant);

    private static readonly Regex DenegacionTotal = new(@"\s+AND false(?=;)", RegexOptions.CultureInvariant);

    private async Task<Guid?> EvaluarTenantContratistaAsync(NpgsqlConnection conexion, NpgsqlTransaction transaccion, DateOnly? hoy = null)
    {
        await using var comando = new NpgsqlCommand(
            "SELECT public.app_inbound_tenant_contratista_del_contrato(@lector, @contrato, @hoy)", conexion, transaccion);
        comando.Parameters.AddWithValue("lector", _lector.Id);
        comando.Parameters.AddWithValue("contrato", _vigente.Id);
        comando.Parameters.AddWithValue("hoy", hoy ?? _hoy);
        return await comando.ExecuteScalarAsync() is Guid tenant ? tenant : null;
    }

    private static async Task<FuncionInbound> TenantContratistaDelContratoAsync(NpgsqlConnection conexion, NpgsqlTransaction transaccion)
    {
        await using var comando = new NpgsqlCommand(
            """
            SELECT pg_get_functiondef(oid), pg_get_userbyid(proowner) || ' ' || COALESCE(proacl::text, '')
            FROM pg_proc
            WHERE oid = 'public.app_inbound_tenant_contratista_del_contrato(uuid,uuid,date)'::regprocedure
            """, conexion, transaccion);
        await using var fila = await comando.ExecuteReaderAsync();
        (await fila.ReadAsync()).Should().BeTrue();
        return new FuncionInbound(fila.GetString(0), fila.GetString(1));
    }

    // La misma línea Inbound para X, Y y Z, pagada por el mismo Pagador TALVEG: el Tenant beneficiario lo sella el contexto.
    private static ProductoContratado LineaInboundVigente() => ProductoContratado.Contratar(LineaProductoContratado.Inbound,
        PagadorDelLector, TramoTrabajadoresActivos.De1A10, ModalidadSuscripcionAlta.Anual, DateTime.UtcNow.AddDays(-30), DateTime.UtcNow);

    private async Task FinalizarLineaInboundDelLectorAsync()
    {
        await using var contexto = Contexto(_lector.Id);
        var linea = await contexto.ProductosContratados.SingleAsync(p => p.Id == _lineaInbound);
        linea.Finalizar(DateTime.UtcNow.AddSeconds(-1), DateTime.UtcNow);
        await contexto.SaveChangesAsync();
    }

    private async Task ComprobarSinLecturaAsync(Guid tenant, Guid usuario, string motivo)
    {
        await using (var contexto = Contexto(tenant, usuario))
        {
            var lector = new LectorDocumentosInboundEnBase(contexto);
            (await lector.AutorizacionDelContratoAsync(_vigente.Id)).Should().BeNull(motivo);
            (await lector.DocumentosAutorizadosAsync(_vigente.Id)).Should().BeEmpty(motivo);
            (await lector.ArchivoAutorizadoAsync(_vigente.Id, _deTrabajador)).Should().BeNull(motivo);
        }

        (await AccesosEnElTenantPropietarioAsync()).Should().BeEmpty("una lectura que el gate niega no deja traza");
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
