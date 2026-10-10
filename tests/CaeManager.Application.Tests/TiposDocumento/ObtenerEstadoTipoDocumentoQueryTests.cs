using CaeManager.Application.Centros;
using CaeManager.Domain.Common;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.TiposDocumento.Queries.ObtenerEstadoTipoDocumento;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.TiposDocumento;

/// <summary>
/// La consulta de la página «Tipo de documento 360». Dos contratos: quién la lee (todos los roles salvo Consulta y
/// Cliente, decidido el 2026-10-08, y solo sobre los Centros de su alcance) y cómo cuenta (un par por Centro × Trabajador;
/// la fila lleva el peor de sus Centros).
/// </summary>
public class ObtenerEstadoTipoDocumentoQueryTests
{
    private sealed class CalculoFalso(params ParDocumentalExigido[] pares) : ICalculoEstadoCentroService
    {
        public List<IReadOnlyList<Guid>> CentrosPedidos { get; } = [];

        public Task<IReadOnlyDictionary<Guid, ResultadoEstadoCentro>> CalcularAsync(
            IReadOnlyList<Guid> centroIds, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<Guid, FraccionCumplimiento>> CalcularCumplimientoAsync(
            IReadOnlyList<Guid> centroIds, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<ParDocumentalExigido>> ObtenerParesExigidosAsync(
            IReadOnlyList<Guid> centroIds, CancellationToken cancellationToken)
        {
            CentrosPedidos.Add(centroIds);
            return Task.FromResult<IReadOnlyList<ParDocumentalExigido>>(pares.Where(p => centroIds.Contains(p.CentroId)).ToList());
        }
    }

    private sealed class Escenario
    {
        public TiposDocumentoQueryContextFalso Tipos { get; } = new();
        public CentrosQueryContextFalso Centros { get; } = new();
        public TrabajadoresQueryContextFalso Trabajadores { get; } = new();
        public EmpresasQueryContextFalso Empresas { get; } = new();
        public DocumentosQueryContextFalso Documentos { get; } = new();

        public Empresa Empresa { get; } = new("Montajes Skynet S.L.");
        public Empresa ClienteEmpresarial { get; } = Empresa.CrearComoCliente("Cyberdyne Ibérica S.A.", "B12345674", false, null, null);
        public TipoDocumento Epi { get; } = new("Entrega de EPI", 12, true, 1, AmbitoAplicacion.Trabajador, RequisitoDocumental.Si);
        public TipoDocumento OtroTipo { get; } = new("Aptitud médica", 12, true, 2, AmbitoAplicacion.Trabajador, RequisitoDocumental.Si);

        public Escenario()
        {
            Empresas.ListaEmpresas.AddRange([Empresa, ClienteEmpresarial]);
            Tipos.ListaTiposDocumento.AddRange([Epi, OtroTipo]);
        }

        public Centro Centro(string nombre)
        {
            var centro = new Centro(ClienteEmpresarial.Id, Empresa.Id, nombre);
            Centros.ListaCentros.Add(centro);
            return centro;
        }

        private int _dnis;

        /// <summary>DNI con letra de control válida: el dominio rechaza los demás.</summary>
        public static string Dni(int numero) => $"{numero:D8}{"TRWAGMYFPDXBNJZSQVHLCKE"[numero % 23]}";

        public Trabajador Trabajador(string nombre, string apellidos)
        {
            var trabajador = CaeManager.Domain.Trabajadores.Trabajador.DeEmpresa(Empresa.Id, nombre, apellidos, Dni(60005000 + _dnis++));
            Trabajadores.ListaTrabajadores.Add(trabajador);
            return trabajador;
        }

        public ParDocumentalExigido Par(Centro centro, Trabajador trabajador, EstadoDocumento estado, TipoDocumento? tipo = null) =>
            new(centro.Id, ClienteEmpresarial.Id, Empresa.Id, trabajador.Id, (tipo ?? Epi).Id, estado);

        public ObtenerEstadoTipoDocumentoQueryHandler Handler(
            CalculoFalso calculo, string? rol = "GestorCae", IReadOnlyList<Guid>? centroIdsVisibles = null) =>
            new(Tipos, Centros, Trabajadores, Empresas, Documentos, calculo,
                new AlcanceDatosServiceFalso(tieneAccesoTotal: centroIdsVisibles is null, centroIdsVisibles: centroIdsVisibles),
                new CurrentUserServiceFalso(rol: rol));
    }

    [Theory]
    [InlineData("Consulta")]
    [InlineData("Cliente")]
    [InlineData("RolQueNoExiste")]
    [InlineData(null)]
    public async Task Ni_Consulta_ni_Cliente_ni_un_rol_desconocido_leen_la_pagina_y_no_se_calcula_nada(string? rol)
    {
        var escenario = new Escenario();
        var centro = escenario.Centro("Almacén Vigo");
        var calculo = new CalculoFalso(escenario.Par(centro, escenario.Trabajador("Pedro", "Gil Mora"), EstadoDocumento.Vencido));

        var dto = await escenario.Handler(calculo, rol).Handle(new ObtenerEstadoTipoDocumentoQuery(escenario.Epi.Id), CancellationToken.None);

        dto.Should().BeNull("un rol sin acceso recibe lo mismo que un tipo inexistente");
        calculo.CentrosPedidos.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Administrador")]
    [InlineData("DireccionCae")]
    [InlineData("CoordinadorCae")]
    [InlineData("GestorCae")]
    public async Task Los_cuatro_roles_de_gestion_la_leen(string rol)
    {
        var escenario = new Escenario();
        var centro = escenario.Centro("Almacén Vigo");
        var calculo = new CalculoFalso(escenario.Par(centro, escenario.Trabajador("Pedro", "Gil Mora"), EstadoDocumento.Vencido));

        var dto = await escenario.Handler(calculo, rol).Handle(new ObtenerEstadoTipoDocumentoQuery(escenario.Epi.Id), CancellationToken.None);

        dto.Should().NotBeNull();
        dto!.Trabajadores.Should().Be(1);
    }

    [Fact]
    public async Task Un_tipo_que_no_existe_y_uno_que_no_se_pide_a_trabajadores_responden_igual_que_sin_acceso()
    {
        var escenario = new Escenario();
        var deEmpresa = new TipoDocumento("Certificado de la Seguridad Social", 1, true, 3, AmbitoAplicacion.Empresa, RequisitoDocumental.Si);
        escenario.Tipos.ListaTiposDocumento.Add(deEmpresa);
        var handler = escenario.Handler(new CalculoFalso());

        (await handler.Handle(new ObtenerEstadoTipoDocumentoQuery(Guid.NewGuid()), CancellationToken.None)).Should().BeNull();
        (await handler.Handle(new ObtenerEstadoTipoDocumentoQuery(deEmpresa.Id), CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Solo_se_piden_y_se_cuentan_los_Centros_del_alcance_de_quien_pregunta()
    {
        var escenario = new Escenario();
        var enCartera = escenario.Centro("Almacén Vigo");
        var ajeno = escenario.Centro("Sede Sevilla");
        var pedro = escenario.Trabajador("Pedro", "Gil Mora");
        var paula = escenario.Trabajador("Paula", "Campos Lara");
        var calculo = new CalculoFalso(
            escenario.Par(enCartera, pedro, EstadoDocumento.Vigente),
            escenario.Par(ajeno, pedro, EstadoDocumento.Vencido),
            escenario.Par(ajeno, paula, EstadoDocumento.Faltante));

        var dto = await escenario.Handler(calculo, centroIdsVisibles: [enCartera.Id])
            .Handle(new ObtenerEstadoTipoDocumentoQuery(escenario.Epi.Id), CancellationToken.None);

        calculo.CentrosPedidos.Should().ContainSingle().Which.Should().Equal(enCartera.Id);
        dto!.Cumplimiento.Should().Be(new FraccionCumplimiento(1, 1));
        dto.Filas.Should().ContainSingle().Which.Nombre.Should().Be("Pedro Gil Mora");
        dto.Filas[0].Centros.Should().ContainSingle().Which.CentroNombre.Should().Be("Almacén Vigo");
    }

    [Fact]
    public async Task Cuenta_un_par_por_Centro_y_la_fila_lleva_el_peor_de_sus_Centros()
    {
        var escenario = new Escenario();
        var vigo = escenario.Centro("Almacén Vigo");
        var bilbao = escenario.Centro("Planta Bilbao");
        var murcia = escenario.Centro("Planta Murcia");
        var mateo = escenario.Trabajador("Mateo", "Soler Vidal");
        var irene = escenario.Trabajador("Irene", "Navarro Gil");
        var carla = escenario.Trabajador("Carla", "Molina Ríos");
        var calculo = new CalculoFalso(
            escenario.Par(vigo, mateo, EstadoDocumento.Vigente),
            escenario.Par(bilbao, mateo, EstadoDocumento.Vencido),
            escenario.Par(murcia, mateo, EstadoDocumento.Vigente),
            escenario.Par(murcia, irene, EstadoDocumento.SinCaducidad),
            escenario.Par(vigo, carla, EstadoDocumento.Urgente),
            // Otro tipo en el mismo Centro: no es de esta página.
            escenario.Par(vigo, carla, EstadoDocumento.Faltante, escenario.OtroTipo));

        var dto = await escenario.Handler(calculo).Handle(new ObtenerEstadoTipoDocumentoQuery(escenario.Epi.Id), CancellationToken.None);

        // Mateo está al día en 2 de 3 Centros: suma 2 de 3, no un completo.
        dto!.Cumplimiento.Should().Be(new FraccionCumplimiento(4, 5));
        dto.Centros.Should().Be(3);
        dto.Trabajadores.Should().Be(3);
        dto.Filas.Select(f => (f.Nombre, f.PeorEstado, f.CentrosAlDia, f.Centros.Count)).Should().Equal(
            ("Mateo Soler Vidal", EstadoDocumento.Vencido, 2, 3),
            ("Carla Molina Ríos", EstadoDocumento.Urgente, 1, 1),
            ("Irene Navarro Gil", EstadoDocumento.SinCaducidad, 1, 1));
        dto.Filas[0].Centros[0].CentroNombre.Should().Be("Planta Bilbao", "los Centros de la fila van del peor estado al mejor");
        dto.Filas[0].Dni.Should().Be(Escenario.Dni(60005000));
        dto.Filas[0].EmpresaNombre.Should().Be("Montajes Skynet S.L.");
        dto.Recuentos.Should().Equal(
            new RecuentoGrupoEstadoDto(GrupoEstadoTipoDocumento.Vencido, 1),
            new RecuentoGrupoEstadoDto(GrupoEstadoTipoDocumento.PorVencer, 1),
            new RecuentoGrupoEstadoDto(GrupoEstadoTipoDocumento.Vigente, 1));
    }

    [Fact]
    public async Task El_filtro_de_estados_y_la_pagina_recortan_las_filas_pero_no_el_anillo_ni_los_recuentos()
    {
        var escenario = new Escenario();
        var vigo = escenario.Centro("Almacén Vigo");
        var pares = new List<ParDocumentalExigido>();
        for (var i = 0; i < 5; i++)
            pares.Add(escenario.Par(vigo, escenario.Trabajador($"Vencido{i}", "Apellido"), EstadoDocumento.Vencido));
        pares.Add(escenario.Par(vigo, escenario.Trabajador("Sin", "Documento"), EstadoDocumento.Faltante));
        pares.Add(escenario.Par(vigo, escenario.Trabajador("Al", "Día"), EstadoDocumento.Vigente));
        var handler = escenario.Handler(new CalculoFalso([.. pares]));

        var dto = await handler.Handle(
            new ObtenerEstadoTipoDocumentoQuery(
                escenario.Epi.Id, [GrupoEstadoTipoDocumento.Vencido, GrupoEstadoTipoDocumento.Pendiente], Pagina: 2, TamanoPagina: 4),
            CancellationToken.None);

        dto!.TotalFiltradas.Should().Be(6);
        dto.Pagina.Should().Be(2);
        dto.Filas.Select(f => f.Nombre).Should().Equal("Vencido4 Apellido", "Sin Documento");
        dto.Cumplimiento.Should().Be(new FraccionCumplimiento(1, 7));
        dto.Trabajadores.Should().Be(7);
        dto.Recuentos.Sum(r => r.Filas).Should().Be(7);

        var fueraDeRango = await handler.Handle(
            new ObtenerEstadoTipoDocumentoQuery(escenario.Epi.Id, Pagina: 99, TamanoPagina: 4), CancellationToken.None);
        fueraDeRango!.Pagina.Should().Be(2, "una página que ya no existe cae en la última, no en una lista vacía");
    }

    [Fact]
    public async Task Sin_ningun_Centro_que_lo_exija_el_anillo_no_mide_nada()
    {
        var escenario = new Escenario();
        escenario.Centro("Almacén Vigo");

        var dto = await escenario.Handler(new CalculoFalso()).Handle(new ObtenerEstadoTipoDocumentoQuery(escenario.Epi.Id), CancellationToken.None);

        dto!.Cumplimiento.Should().Be(FraccionCumplimiento.SinRequisitos);
        dto.Cumplimiento.Porcentaje.Should().BeNull();
        dto.Filas.Should().BeEmpty();
        dto.Recuentos.Should().BeEmpty();
    }

    [Theory]
    [InlineData(EstadoDocumento.Vencido, GrupoEstadoTipoDocumento.Vencido)]
    [InlineData(EstadoDocumento.Faltante, GrupoEstadoTipoDocumento.Pendiente)]
    [InlineData(EstadoDocumento.EnTolerancia, GrupoEstadoTipoDocumento.EnTolerancia)]
    [InlineData(EstadoDocumento.Urgente, GrupoEstadoTipoDocumento.PorVencer)]
    [InlineData(EstadoDocumento.Proximo, GrupoEstadoTipoDocumento.PorVencer)]
    [InlineData(EstadoDocumento.SinConfirmar, GrupoEstadoTipoDocumento.SinConfirmar)]
    [InlineData(EstadoDocumento.Vigente, GrupoEstadoTipoDocumento.Vigente)]
    [InlineData(EstadoDocumento.SinCaducidad, GrupoEstadoTipoDocumento.Vigente)]
    public void Cada_estado_cae_en_una_de_las_seis_palabras(EstadoDocumento estado, GrupoEstadoTipoDocumento grupo) =>
        EstadoTipoDocumentoCalculo.Grupo(estado).Should().Be(grupo);

    [Fact]
    public void El_orden_del_peor_al_mejor_cubre_todos_los_estados_y_es_el_de_la_decision()
    {
        Enum.GetValues<EstadoDocumento>().OrderBy(EstadoTipoDocumentoCalculo.Gravedad).Should().Equal(
            EstadoDocumento.Vencido, EstadoDocumento.Faltante, EstadoDocumento.EnTolerancia, EstadoDocumento.Urgente,
            EstadoDocumento.Proximo, EstadoDocumento.SinConfirmar, EstadoDocumento.Vigente, EstadoDocumento.SinCaducidad);
    }

    [Fact]
    public async Task La_fila_lleva_el_documento_del_tipo_y_la_tolerancia_es_la_del_Cliente_empresarial_de_cada_Centro()
    {
        var escenario = new Escenario();
        var hoy = DiaDeNegocio.Hoy();
        var otroCliente = Empresa.CrearComoCliente("Tyrell Logística S.A.", "B12345674", false, null, null);
        escenario.Empresas.ListaEmpresas.Add(otroCliente);
        var vigo = escenario.Centro("Almacén Vigo");
        var sevilla = new Centro(otroCliente.Id, escenario.Empresa.Id, "Sede Sevilla");
        escenario.Centros.ListaCentros.Add(sevilla);
        var mateo = escenario.Trabajador("Mateo", "Soler Vidal");

        // Vencido hace 3 días. El Cliente empresarial de Vigo tolera 10 días para este tipo; el de Sevilla, ninguno.
        var epi = Documento.DeTrabajador(mateo.Id, escenario.Epi.Id, hoy.AddDays(-368), VigenciaDocumento.VenceEl(hoy.AddDays(-3)));
        // Documento de OTRO tipo del mismo Trabajador, y tolerancia de OTRO tipo: no son de esta página.
        var aptitud = Documento.DeTrabajador(mateo.Id, escenario.OtroTipo.Id, hoy.AddDays(-10), VigenciaDocumento.VenceEl(hoy.AddDays(300)));
        escenario.Documentos.ListaDocumentos.AddRange([aptitud, epi]);
        escenario.Tipos.ListaToleranciasDocumentoClienteEmpresarial.AddRange([
            new ToleranciaDocumentoClienteEmpresarial(escenario.ClienteEmpresarial.Id, escenario.Epi.Id, 10),
            new ToleranciaDocumentoClienteEmpresarial(otroCliente.Id, escenario.OtroTipo.Id, 30)]);
        var calculo = new CalculoFalso(
            escenario.Par(vigo, mateo, EstadoDocumento.Vencido),
            new ParDocumentalExigido(sevilla.Id, otroCliente.Id, escenario.Empresa.Id, mateo.Id, escenario.Epi.Id, EstadoDocumento.Vencido));

        var dto = await escenario.Handler(calculo).Handle(new ObtenerEstadoTipoDocumentoQuery(escenario.Epi.Id), CancellationToken.None);

        var fila = dto!.Filas.Should().ContainSingle().Subject;
        fila.DocumentoId.Should().Be(epi.Id, "la fecha pintada es la del documento de este tipo, no la de otro documento del Trabajador");
        fila.FechaEmision.Should().Be(hoy.AddDays(-368));
        fila.FechaVencimiento.Should().Be(hoy.AddDays(-3));
        fila.PeorEstado.Should().Be(EstadoDocumento.Vencido, "en Sevilla nadie lo tolera, y la fila lleva el peor de sus Centros");
        fila.Centros.Select(c => (c.CentroNombre, c.Estado, c.EnToleranciaHasta)).Should().Equal(
            ("Sede Sevilla", EstadoDocumento.Vencido, (DateOnly?)null),
            ("Almacén Vigo", EstadoDocumento.EnTolerancia, (DateOnly?)hoy.AddDays(7)));
        // Hoy ningún porcentaje cuenta «En tolerancia» como al día (CumplimientoDocumental): el anillo dice lo mismo que el resto.
        dto.Cumplimiento.Should().Be(new FraccionCumplimiento(0, 2));
    }
}
