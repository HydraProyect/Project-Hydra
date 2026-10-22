using CaeManager.Application.Centros.Queries.ObtenerCambiosEstadoPlataformaDeCentro;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Application.Vehiculos;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Trabajadores;
using CaeManager.Domain.Vehiculos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Documentos;

/// <summary>
/// Historial de Centro 360, sección de cambios de estado en la plataforma CAE del Cliente: solo salen los cambios del
/// Centro pedido, solo los de un documento cuyo titular (Trabajador, Empresa o Vehículo) está en la cartera del usuario,
/// y ese alcance se aplica ANTES de cortar en <c>Maximo</c>. El motivo, la nota y el autor solo los leen los roles de
/// gestión (los mismos que leen las reclamaciones): Consulta y el Usuario de Cliente ven el cambio sin ese detalle.
/// </summary>
public class ObtenerCambiosEstadoPlataformaDeCentroQueryTests
{
    private static readonly DateTime Ahora = new(2026, 10, 21, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Hoy = new(2026, 10, 21);

    private sealed class VehiculosFalsos : IVehiculosQueryContext
    {
        public List<Vehiculo> Lista { get; } = [];
        public IQueryable<Vehiculo> Vehiculos => new TestAsyncQueryable<Vehiculo>(Lista.AsQueryable());
    }

    private sealed class Escenario
    {
        public DocumentosQueryContextFalso Documentos { get; } = new();
        public CentrosQueryContextFalso Centros { get; } = new();
        public TiposDocumentoQueryContextFalso Tipos { get; } = new();
        public TrabajadoresQueryContextFalso Trabajadores { get; } = new();
        public EmpresasQueryContextFalso Empresas { get; } = new();
        public VehiculosFalsos Vehiculos { get; } = new();
        public ProveedoresPlataformaCaeQueryContextFalso Proveedores { get; } = new();
        public TipoDocumento Tipo { get; } = new("Formación PRL", null, false, 1, AmbitoAplicacion.Trabajador);
        public ProveedorPlataformaCae Plataforma { get; } = new("nalanda", "Nalanda");
        public Guid CentroId { get; } = Guid.NewGuid();
        public Guid UsuarioAutorId { get; } = Guid.NewGuid();

        public Escenario()
        {
            Tipos.ListaTiposDocumento.Add(Tipo);
            Proveedores.ListaProveedores.Add(Plataforma);
        }

        public ObtenerCambiosEstadoPlataformaDeCentroQueryHandler Handler(
            AlcanceDatosServiceFalso? alcance = null, string? rol = "GestorCae") => new(
            Documentos, Centros, Tipos, Trabajadores, Empresas, Vehiculos, Proveedores,
            alcance ?? new AlcanceDatosServiceFalso(), new DirectorioUsuariosServiceFalso(), new CurrentUserServiceFalso(Guid.NewGuid(), rol));

        public Trabajador ConTrabajador(string nombre)
        {
            var trabajador = Trabajador.DeEmpresa(Guid.NewGuid(), nombre, "Pérez", "12345678Z");
            Trabajadores.ListaTrabajadores.Add(trabajador);
            return trabajador;
        }

        public Empresa ConEmpresa(string razonSocial)
        {
            var empresa = new Empresa(razonSocial);
            Empresas.ListaEmpresas.Add(empresa);
            return empresa;
        }

        public Vehiculo ConVehiculo(string nombre)
        {
            var vehiculo = Vehiculo.DeEmpresa(Guid.NewGuid(), nombre, "Modelo", "1234ABC");
            Vehiculos.Lista.Add(vehiculo);
            return vehiculo;
        }

        public CanalGestionDocumental Canal(Guid? centroId = null)
        {
            var canal = CanalGestionDocumental.DePlataforma(centroId ?? CentroId, "Acceso", Plataforma.Id, null, null, null);
            Centros.ListaCanalesGestionDocumental.Add(canal);
            return canal;
        }

        /// <summary>Anota a mano un cambio sobre el documento dado, en el canal dado, y lo deja «guardado» en el contexto.</summary>
        public CambioEstadoAcreditacionPlataforma Cambio(
            Documento documento, CanalGestionDocumental canal, EstadoAcreditacion destino, DateTime momentoUtc,
            string? motivo = null, string? nota = null)
        {
            var acreditacion = new AcreditacionDocumentoPlataforma(documento.Id, canal.Id);
            Documentos.ListaDocumentos.Add(documento);
            Documentos.ListaAcreditacionesDocumentoPlataforma.Add(acreditacion);
            var cambio = acreditacion.CambiarEstadoAMano(destino, Hoy, motivo, nota, UsuarioAutorId, momentoUtc);
            Documentos.ListaCambiosEstadoAcreditacionPlataforma.Add(cambio);
            return cambio;
        }

        public Documento DocumentoDe(Trabajador trabajador) =>
            Documento.DeTrabajador(trabajador.Id, Tipo.Id, new DateOnly(2026, 1, 1), VigenciaDocumento.NoCaduca);
    }

    [Fact]
    public async Task Solo_salen_los_cambios_del_Centro_pedido()
    {
        var escenario = new Escenario();
        var otroCentro = Guid.NewGuid();
        var ana = escenario.ConTrabajador("Ana");
        var luis = escenario.ConTrabajador("Luis");
        var delCentro = escenario.Cambio(escenario.DocumentoDe(ana), escenario.Canal(), EstadoAcreditacion.Subida, Ahora);
        escenario.Cambio(escenario.DocumentoDe(luis), escenario.Canal(otroCentro), EstadoAcreditacion.Subida, Ahora.AddMinutes(5));

        var resultado = await escenario.Handler().Handle(new ObtenerCambiosEstadoPlataformaDeCentroQuery(escenario.CentroId), CancellationToken.None);

        resultado.Should().ContainSingle().Which.Id.Should().Be(delCentro.Id);
        resultado[0].TitularNombre.Should().Be("Ana Pérez");
        resultado[0].NombrePlataforma.Should().Be("Nalanda");
        resultado[0].DocumentoNombre.Should().Be("Formación PRL");
    }

    [Fact]
    public async Task Un_Centro_fuera_de_la_cartera_del_usuario_no_devuelve_nada()
    {
        var escenario = new Escenario();
        var ana = escenario.ConTrabajador("Ana");
        escenario.Cambio(escenario.DocumentoDe(ana), escenario.Canal(), EstadoAcreditacion.Subida, Ahora);
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, centroIdsVisibles: [Guid.NewGuid()]);

        var resultado = await escenario.Handler(alcance).Handle(new ObtenerCambiosEstadoPlataformaDeCentroQuery(escenario.CentroId), CancellationToken.None);

        resultado.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_Centro_dentro_de_la_cartera_si_devuelve_sus_cambios()
    {
        var escenario = new Escenario();
        var ana = escenario.ConTrabajador("Ana");
        escenario.Cambio(escenario.DocumentoDe(ana), escenario.Canal(), EstadoAcreditacion.Subida, Ahora);
        var alcance = new AlcanceDatosServiceFalso(
            tieneAccesoTotal: false, centroIdsVisibles: [escenario.CentroId], trabajadorIdsVisibles: [ana.Id]);

        var resultado = await escenario.Handler(alcance).Handle(new ObtenerCambiosEstadoPlataformaDeCentroQuery(escenario.CentroId), CancellationToken.None);

        resultado.Should().ContainSingle();
    }

    [Fact]
    public async Task El_titular_fuera_de_cartera_no_aparece_sea_Trabajador_Empresa_o_Vehiculo()
    {
        var escenario = new Escenario();
        var canal = escenario.Canal();
        var visible = escenario.ConTrabajador("Ana");
        var oculto = escenario.ConTrabajador("Luis");
        var empresaOculta = escenario.ConEmpresa("Contrata Oculta S.L.");
        var vehiculoOculto = escenario.ConVehiculo("Furgoneta oculta");
        var empresaVisible = escenario.ConEmpresa("Contrata Visible S.L.");
        var vehiculoVisible = escenario.ConVehiculo("Furgoneta visible");
        escenario.Cambio(escenario.DocumentoDe(visible), canal, EstadoAcreditacion.Subida, Ahora);
        escenario.Cambio(escenario.DocumentoDe(oculto), canal, EstadoAcreditacion.Subida, Ahora.AddMinutes(1));
        escenario.Cambio(
            Documento.DeEmpresa(empresaOculta.Id, escenario.Tipo.Id, new DateOnly(2026, 1, 1), VigenciaDocumento.NoCaduca),
            canal, EstadoAcreditacion.Subida, Ahora.AddMinutes(2));
        escenario.Cambio(
            Documento.DeVehiculo(vehiculoOculto.Id, escenario.Tipo.Id, new DateOnly(2026, 1, 1), VigenciaDocumento.NoCaduca),
            canal, EstadoAcreditacion.Subida, Ahora.AddMinutes(3));
        escenario.Cambio(
            Documento.DeEmpresa(empresaVisible.Id, escenario.Tipo.Id, new DateOnly(2026, 1, 1), VigenciaDocumento.NoCaduca),
            canal, EstadoAcreditacion.Subida, Ahora.AddMinutes(-1));
        escenario.Cambio(
            Documento.DeVehiculo(vehiculoVisible.Id, escenario.Tipo.Id, new DateOnly(2026, 1, 1), VigenciaDocumento.NoCaduca),
            canal, EstadoAcreditacion.Subida, Ahora.AddMinutes(-2));
        var alcance = new AlcanceDatosServiceFalso(
            tieneAccesoTotal: false, centroIdsVisibles: [escenario.CentroId], trabajadorIdsVisibles: [visible.Id],
            empresaIdsVisibles: [empresaVisible.Id], vehiculoIdsVisibles: [vehiculoVisible.Id]);

        var resultado = await escenario.Handler(alcance).Handle(new ObtenerCambiosEstadoPlataformaDeCentroQuery(escenario.CentroId), CancellationToken.None);

        resultado.Select(c => c.TitularNombre).Should().BeEquivalentTo("Ana Pérez", "Contrata Visible S.L.", "Furgoneta visible");
    }

    [Fact]
    public async Task El_alcance_se_aplica_antes_de_cortar_en_Maximo()
    {
        var escenario = new Escenario();
        var canal = escenario.Canal();
        var visible = escenario.ConTrabajador("Ana");
        // Los cambios más recientes son de titulares fuera de cartera: si el corte fuera antes del filtro, saldría vacío.
        var antiguoVisible = escenario.Cambio(escenario.DocumentoDe(visible), canal, EstadoAcreditacion.Subida, Ahora.AddDays(-3));
        for (var i = 0; i < 3; i++)
        {
            var ajeno = escenario.ConTrabajador($"Ajeno{i}");
            escenario.Cambio(escenario.DocumentoDe(ajeno), canal, EstadoAcreditacion.Subida, Ahora.AddMinutes(i));
        }

        var alcance = new AlcanceDatosServiceFalso(
            tieneAccesoTotal: false, centroIdsVisibles: [escenario.CentroId], trabajadorIdsVisibles: [visible.Id]);

        var resultado = await escenario.Handler(alcance).Handle(new ObtenerCambiosEstadoPlataformaDeCentroQuery(escenario.CentroId, Maximo: 2), CancellationToken.None);

        resultado.Should().ContainSingle().Which.Id.Should().Be(antiguoVisible.Id);
    }

    [Fact]
    public async Task Sale_el_mas_reciente_primero_con_los_deshechos_marcados_y_respeta_el_maximo()
    {
        var escenario = new Escenario();
        var canal = escenario.Canal();
        var ana = escenario.ConTrabajador("Ana");
        var documento = escenario.DocumentoDe(ana);
        var primero = escenario.Cambio(documento, canal, EstadoAcreditacion.Subida, Ahora);
        var acreditacion = escenario.Documentos.ListaAcreditacionesDocumentoPlataforma.Single();
        acreditacion.DeshacerCambioEstado(primero.Id, Ahora.AddMinutes(1));
        var segundo = acreditacion.CambiarEstadoAMano(EstadoAcreditacion.Aceptada, Hoy, null, null, escenario.UsuarioAutorId, Ahora.AddMinutes(2));
        escenario.Documentos.ListaCambiosEstadoAcreditacionPlataforma.Add(segundo);

        var todos = await escenario.Handler().Handle(new ObtenerCambiosEstadoPlataformaDeCentroQuery(escenario.CentroId), CancellationToken.None);
        var unico = await escenario.Handler().Handle(new ObtenerCambiosEstadoPlataformaDeCentroQuery(escenario.CentroId, Maximo: 1), CancellationToken.None);

        todos.Select(c => c.Id).Should().Equal(segundo.Id, primero.Id);
        todos.Select(c => c.Deshecho).Should().Equal(false, true);
        unico.Should().ContainSingle().Which.Id.Should().Be(segundo.Id);
    }

    [Fact]
    public async Task Una_lista_sin_restriccion_de_un_tipo_de_titular_no_deja_entrar_a_los_otros_tipos_fuera_de_cartera()
    {
        var escenario = new Escenario();
        var canal = escenario.Canal();
        var ana = escenario.ConTrabajador("Ana");
        var empresaOculta = escenario.ConEmpresa("Contrata Oculta S.L.");
        var empresaVisible = escenario.ConEmpresa("Contrata Visible S.L.");
        escenario.Cambio(escenario.DocumentoDe(ana), canal, EstadoAcreditacion.Subida, Ahora);
        escenario.Cambio(
            Documento.DeEmpresa(empresaOculta.Id, escenario.Tipo.Id, new DateOnly(2026, 1, 1), VigenciaDocumento.NoCaduca),
            canal, EstadoAcreditacion.Subida, Ahora.AddMinutes(1));
        escenario.Cambio(
            Documento.DeEmpresa(empresaVisible.Id, escenario.Tipo.Id, new DateOnly(2026, 1, 1), VigenciaDocumento.NoCaduca),
            canal, EstadoAcreditacion.Subida, Ahora.AddMinutes(2));
        // Trabajadores sin restricción (null) y Empresas restringidas: la rama «sin restricción» es solo de los Trabajadores.
        var alcance = new AlcanceDatosServiceFalso(
            tieneAccesoTotal: false, centroIdsVisibles: [escenario.CentroId], trabajadorIdsVisibles: null,
            empresaIdsVisibles: [empresaVisible.Id]);

        var resultado = await escenario.Handler(alcance).Handle(new ObtenerCambiosEstadoPlataformaDeCentroQuery(escenario.CentroId), CancellationToken.None);

        resultado.Select(c => c.TitularNombre).Should().BeEquivalentTo("Ana Pérez", "Contrata Visible S.L.");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task Un_maximo_no_positivo_devuelve_como_minimo_el_ultimo_cambio(int maximo)
    {
        var escenario = new Escenario();
        var canal = escenario.Canal();
        var ana = escenario.ConTrabajador("Ana");
        escenario.Cambio(escenario.DocumentoDe(ana), canal, EstadoAcreditacion.Subida, Ahora);
        var reciente = escenario.Cambio(escenario.DocumentoDe(ana), canal, EstadoAcreditacion.Subida, Ahora.AddMinutes(1));

        var resultado = await escenario.Handler().Handle(new ObtenerCambiosEstadoPlataformaDeCentroQuery(escenario.CentroId, maximo), CancellationToken.None);

        resultado.Should().ContainSingle().Which.Id.Should().Be(reciente.Id);
    }

    [Fact]
    public async Task Un_maximo_enorme_se_acota_a_doscientos()
    {
        var escenario = new Escenario();
        var canal = escenario.Canal();
        var ana = escenario.ConTrabajador("Ana");
        for (var i = 0; i < 205; i++)
            escenario.Cambio(escenario.DocumentoDe(ana), canal, EstadoAcreditacion.Subida, Ahora.AddMinutes(i));

        var resultado = await escenario.Handler().Handle(new ObtenerCambiosEstadoPlataformaDeCentroQuery(escenario.CentroId, 10_000), CancellationToken.None);

        resultado.Should().HaveCount(200);
    }

    [Fact]
    public async Task Un_autor_que_el_directorio_no_resuelve_deja_el_cambio_sin_nombre_pero_con_su_detalle()
    {
        var escenario = new Escenario();
        var ana = escenario.ConTrabajador("Ana");
        escenario.Cambio(
            escenario.DocumentoDe(ana), escenario.Canal(), EstadoAcreditacion.Rechazada, Ahora,
            motivo: "Falta la firma", nota: "Solicitud 4411");
        var handler = new ObtenerCambiosEstadoPlataformaDeCentroQueryHandler(
            escenario.Documentos, escenario.Centros, escenario.Tipos, escenario.Trabajadores, escenario.Empresas, escenario.Vehiculos,
            escenario.Proveedores, new AlcanceDatosServiceFalso(), new DirectorioUsuariosServiceFalso(esVisible: false),
            new CurrentUserServiceFalso(Guid.NewGuid(), "GestorCae"));

        var resultado = await handler.Handle(new ObtenerCambiosEstadoPlataformaDeCentroQuery(escenario.CentroId), CancellationToken.None);

        var cambio = resultado.Should().ContainSingle().Subject;
        cambio.UsuarioNombre.Should().BeNull();
        cambio.Motivo.Should().Be("Falta la firma");
        cambio.Nota.Should().Be("Solicitud 4411");
    }

    public static TheoryData<string> RolesDeGestion => ["Administrador", "DireccionCae", "CoordinadorCae", "GestorCae"];

    [Theory]
    [MemberData(nameof(RolesDeGestion))]
    public async Task Los_roles_de_gestion_ven_el_motivo_la_nota_y_el_autor(string rol)
    {
        var escenario = new Escenario();
        var ana = escenario.ConTrabajador("Ana");
        escenario.Cambio(
            escenario.DocumentoDe(ana), escenario.Canal(), EstadoAcreditacion.Rechazada, Ahora,
            motivo: "Falta la firma", nota: "Solicitud 4411");

        var resultado = await escenario.Handler(rol: rol).Handle(new ObtenerCambiosEstadoPlataformaDeCentroQuery(escenario.CentroId), CancellationToken.None);

        var cambio = resultado.Should().ContainSingle().Subject;
        cambio.Motivo.Should().Be("Falta la firma");
        cambio.Nota.Should().Be("Solicitud 4411");
        cambio.UsuarioNombre.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("Consulta")]
    [InlineData("Cliente")]
    [InlineData(null)]
    public async Task Consulta_el_Usuario_de_Cliente_y_un_rol_sin_resolver_ven_el_cambio_sin_motivo_nota_ni_autor(string? rol)
    {
        var escenario = new Escenario();
        var ana = escenario.ConTrabajador("Ana");
        escenario.Cambio(
            escenario.DocumentoDe(ana), escenario.Canal(), EstadoAcreditacion.Rechazada, Ahora,
            motivo: "Falta la firma", nota: "Solicitud 4411");

        var resultado = await escenario.Handler(rol: rol).Handle(new ObtenerCambiosEstadoPlataformaDeCentroQuery(escenario.CentroId), CancellationToken.None);

        var cambio = resultado.Should().ContainSingle().Subject;
        cambio.Motivo.Should().BeNull();
        cambio.Nota.Should().BeNull();
        cambio.UsuarioNombre.Should().BeNull();
        // El cambio en sí (qué documento, de quién, a qué estado) sí lo ven: solo se redacta el detalle.
        cambio.Estado.Should().Be(EstadoAcreditacion.Rechazada);
        cambio.EstadoAnterior.Should().Be(EstadoAcreditacion.PendienteDeSubir);
        cambio.TitularNombre.Should().Be("Ana Pérez");
        cambio.DocumentoNombre.Should().Be("Formación PRL");
    }
}