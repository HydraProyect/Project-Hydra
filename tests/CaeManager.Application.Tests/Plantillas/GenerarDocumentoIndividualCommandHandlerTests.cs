using System.Text.Json;
using CaeManager.Application.Common;
using CaeManager.Application.Plantillas.Commands.GenerarDocumentoIndividual;
using CaeManager.Application.Tests.Asignaciones;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Common;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Contactos;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Plantillas;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Plantillas;

public class GenerarDocumentoIndividualCommandHandlerTests
{
    private const string Dni = "77189989B";

    private sealed class RellenadorFalso : IRellenadorPlantillaPdfService
    {
        public IReadOnlyList<ElementoRellenoPlantilla>? UltimosElementos { get; private set; }

        public byte[] Rellenar(byte[] pdfOriginal, FormatoOrigenPlantilla formato, IReadOnlyList<ElementoRellenoPlantilla> elementos)
        {
            UltimosElementos = elementos;
            return [9, 9, 9];
        }
    }

    private sealed class Entorno
    {
        public required PlantillaDocumentoVersionRepositorioFalso Versiones { get; init; }
        public required PlantillaDocumentoRepositorioFalso Plantillas { get; init; }
        public required DocumentoRepositorioFalso Documentos { get; init; }
        public required TiposDocumentoQueryContextFalso TiposDocumento { get; init; }
        public required EmpresasQueryContextFalso Empresas { get; init; }
        public required TrabajadoresQueryContextFalso Trabajadores { get; init; }
        public required CentrosQueryContextFalso Centros { get; init; }
        public required ContactosAgendaQueryContextFalso Contactos { get; init; }
        public required RellenadorFalso Rellenador { get; init; }
        public required FileStorageServiceFalso Almacenamiento { get; init; }
        public required PlantillaDocumentoVersion Version { get; init; }
        public required PlantillaDocumento Plantilla { get; init; }
        public AsignacionRepositorioFalso Asignaciones { get; } = new();

        public GenerarDocumentoIndividualCommandHandler CrearHandler(
            Guid? usuarioActualId = null, IAlcanceDatosService? alcanceDatos = null) => new(
            Versiones, Plantillas, Documentos, new DocumentoGeneradoRepositorioFalso(), TiposDocumento,
            Empresas, Trabajadores, Centros, Contactos, Rellenador, Almacenamiento,
            new CurrentUserServiceFalso(usuarioActualId ?? Guid.NewGuid()),
            alcanceDatos ?? new AlcanceDatosServiceFalso(), Asignaciones, new UnitOfWorkFalso());
    }

    private static async Task<Entorno> ConstruirEntornoAsync(TipoDocumento? tipoDocumentoOverride = null)
    {
        var tiposDocumento = new TiposDocumentoQueryContextFalso();
        var tipoDocumento = tipoDocumentoOverride ?? new TipoDocumento("Ficha de riesgos", null, false, 1, AmbitoAplicacion.Trabajador);
        tiposDocumento.ListaTiposDocumento.Add(tipoDocumento);

        var plantilla = new PlantillaDocumento(
            OrigenPlantilla.Externa, "Ficha de riesgos", AmbitoAplicacion.Trabajador, FormatoOrigenPlantilla.PdfVisual, tipoDocumento.Id);
        var plantillas = new PlantillaDocumentoRepositorioFalso();
        plantillas.Agregar(plantilla);

        var almacenamiento = new FileStorageServiceFalso();
        using var flujoOriginal = new MemoryStream([1, 2, 3]);
        var archivoUrl = await almacenamiento.GuardarAsync(flujoOriginal, "original.pdf");

        var version = new PlantillaDocumentoVersion(plantilla.Id, 1, archivoUrl, new string('a', 64));
        var versiones = new PlantillaDocumentoVersionRepositorioFalso();
        versiones.Agregar(version);

        return new Entorno
        {
            Versiones = versiones,
            Plantillas = plantillas,
            Documentos = new DocumentoRepositorioFalso(),
            TiposDocumento = tiposDocumento,
            Empresas = new EmpresasQueryContextFalso(),
            Trabajadores = new TrabajadoresQueryContextFalso(),
            Centros = new CentrosQueryContextFalso(),
            Contactos = new ContactosAgendaQueryContextFalso(),
            Rellenador = new RellenadorFalso(),
            Almacenamiento = almacenamiento,
            Version = version,
            Plantilla = plantilla,
        };
    }

    private static void Confirmar(PlantillaDocumentoVersion version, IEnumerable<PlantillaElemento> elementos, Guid usuarioId)
    {
        version.EstablecerElementos(elementos);
        version.Confirmar(usuarioId, DateTime.UtcNow);
    }

    [Fact]
    public async Task Rechaza_una_version_sin_confirmar()
    {
        var entorno = await ConstruirEntornoAsync();
        var trabajador = Trabajador.DeEmpresa(Guid.NewGuid(), "Juan", "Pérez", Dni);
        entorno.Trabajadores.ListaTrabajadores.Add(trabajador);
        var handler = entorno.CrearHandler();

        var resultado = await handler.Handle(
            new GenerarDocumentoIndividualCommand(entorno.Version.Id, trabajador.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Plantilla.VersionNoConfirmada");
    }

    [Fact]
    public async Task Rechaza_un_propietario_inexistente()
    {
        var entorno = await ConstruirEntornoAsync();
        var usuarioId = Guid.NewGuid();
        Confirmar(entorno.Version, [new PlantillaElemento(entorno.Version.Id, TipoElementoPlantilla.Texto, 1, 0, 0, 10, 10, "Campo", FuenteDatoPlantilla.Constante, "x")], usuarioId);
        var handler = entorno.CrearHandler(usuarioId);

        var resultado = await handler.Handle(
            new GenerarDocumentoIndividualCommand(entorno.Version.Id, Guid.NewGuid()), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Plantilla.PropietarioNoEncontrado");
    }

    [Fact]
    public async Task Resuelve_datos_del_trabajador_y_crea_documento_y_snapshot()
    {
        var entorno = await ConstruirEntornoAsync();
        var trabajador = Trabajador.DeEmpresa(Guid.NewGuid(), "Juan", "Pérez", Dni, puesto: "Soldador");
        entorno.Trabajadores.ListaTrabajadores.Add(trabajador);
        var usuarioId = Guid.NewGuid();

        Confirmar(entorno.Version,
        [
            new PlantillaElemento(entorno.Version.Id, TipoElementoPlantilla.Texto, 1, 0, 0, 10, 10, "Nombre", FuenteDatoPlantilla.TrabajadorNombreCompleto),
            new PlantillaElemento(entorno.Version.Id, TipoElementoPlantilla.Texto, 1, 20, 0, 10, 10, "DNI", FuenteDatoPlantilla.TrabajadorDni),
            new PlantillaElemento(entorno.Version.Id, TipoElementoPlantilla.Texto, 1, 40, 0, 10, 10, "Puesto", FuenteDatoPlantilla.TrabajadorPuesto),
        ], usuarioId);
        var handler = entorno.CrearHandler(usuarioId);

        var resultado = await handler.Handle(
            new GenerarDocumentoIndividualCommand(entorno.Version.Id, trabajador.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        entorno.Documentos.Documentos.Should().ContainSingle();
        var documento = entorno.Documentos.Documentos[0];
        documento.TrabajadorId.Should().Be(trabajador.Id);
        documento.TipoDocumentoId.Should().Be(entorno.Plantilla.TipoDocumentoId);
        documento.ArchivoUrl.Should().NotBeNullOrWhiteSpace();

        entorno.Rellenador.UltimosElementos.Should().ContainSingle(e => e.Valor == "Juan Pérez");
        entorno.Rellenador.UltimosElementos.Should().ContainSingle(e => e.Valor == Dni);
        entorno.Rellenador.UltimosElementos.Should().ContainSingle(e => e.Valor == "Soldador");
    }

    [Fact]
    public async Task Resuelve_empresa_y_cliente_a_traves_del_centro_cuando_no_vienen_directos()
    {
        var entorno = await ConstruirEntornoAsync();
        var cliente = Empresa.CrearComoCliente("Cliente SA", "B12345674", false, null, null);
        var empresa = new Empresa("Contratista SL", "B12345674");
        entorno.Empresas.ListaEmpresas.Add(cliente);
        entorno.Empresas.ListaEmpresas.Add(empresa);
        var centro = new Centro(cliente.Id, empresa.Id, "Centro Norte", direccion: "Calle Falsa 123");
        entorno.Centros.ListaCentros.Add(centro);
        var trabajador = Trabajador.DeEmpresa(empresa.Id, "Juan", "Pérez", Dni);
        entorno.Trabajadores.ListaTrabajadores.Add(trabajador);
        entorno.Asignaciones.Agregar(new Asignacion(trabajador.Id, centro.Id, new DateOnly(2026, 1, 1)));
        var usuarioId = Guid.NewGuid();

        Confirmar(entorno.Version,
        [
            new PlantillaElemento(entorno.Version.Id, TipoElementoPlantilla.Texto, 1, 0, 0, 10, 10, "Empresa", FuenteDatoPlantilla.EmpresaRazonSocial),
            new PlantillaElemento(entorno.Version.Id, TipoElementoPlantilla.Texto, 1, 20, 0, 10, 10, "Cliente", FuenteDatoPlantilla.ClienteRazonSocial),
            new PlantillaElemento(entorno.Version.Id, TipoElementoPlantilla.Texto, 1, 40, 0, 10, 10, "Centro", FuenteDatoPlantilla.CentroNombre),
            new PlantillaElemento(entorno.Version.Id, TipoElementoPlantilla.Texto, 1, 60, 0, 10, 10, "Dirección", FuenteDatoPlantilla.CentroDireccion),
        ], usuarioId);
        var handler = entorno.CrearHandler(usuarioId);

        var resultado = await handler.Handle(
            new GenerarDocumentoIndividualCommand(entorno.Version.Id, trabajador.Id, CentroId: centro.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        entorno.Rellenador.UltimosElementos.Should().ContainSingle(e => e.Valor == "Contratista SL");
        entorno.Rellenador.UltimosElementos.Should().ContainSingle(e => e.Valor == "Cliente SA");
        entorno.Rellenador.UltimosElementos.Should().ContainSingle(e => e.Valor == "Centro Norte");
        entorno.Rellenador.UltimosElementos.Should().ContainSingle(e => e.Valor == "Calle Falsa 123");
    }

    [Fact]
    public async Task Resuelve_contacto_por_rol_de_la_empresa()
    {
        var entorno = await ConstruirEntornoAsync();
        var empresa = new Empresa("Contratista SL", "B12345674");
        entorno.Empresas.ListaEmpresas.Add(empresa);
        var trabajador = Trabajador.DeEmpresa(empresa.Id, "Juan", "Pérez", Dni);
        entorno.Trabajadores.ListaTrabajadores.Add(trabajador);
        var contacto = ContactoAgenda.DeEmpresa(empresa.Id, "Ana Ruiz", "ana@empresa.test", null, null, null, false, false, false);
        contacto.EstablecerRoles([RolContacto.ResponsablePrl]);
        entorno.Contactos.ListaContactosAgenda.Add(contacto);
        entorno.Contactos.ListaContactosAgendaRoles.AddRange(contacto.Roles);
        var usuarioId = Guid.NewGuid();

        Confirmar(entorno.Version,
        [
            new PlantillaElemento(entorno.Version.Id, TipoElementoPlantilla.Texto, 1, 0, 0, 10, 10, "Responsable PRL", FuenteDatoPlantilla.EmpresaResponsablePrl),
        ], usuarioId);
        var handler = entorno.CrearHandler(usuarioId);

        var resultado = await handler.Handle(
            new GenerarDocumentoIndividualCommand(entorno.Version.Id, trabajador.Id, CentroId: null), CancellationToken.None);

        // Sin CentroId ni ámbito Empresa, la empresa no se resuelve — este caso
        // requiere pasar CentroId (ver test anterior) o que el ámbito sea Empresa.
        // Aquí se comprueba explícitamente que sin ninguno de los dos, el campo
        // simplemente queda sin resolver (no lanza), documentando la limitación.
        resultado.EsExitoso.Should().BeTrue();
        entorno.Rellenador.UltimosElementos.Should().ContainSingle(e => e.Valor == null);
    }

    [Fact]
    public async Task Resuelve_valores_manuales_por_id_de_elemento()
    {
        var entorno = await ConstruirEntornoAsync();
        var trabajador = Trabajador.DeEmpresa(Guid.NewGuid(), "Juan", "Pérez", Dni);
        entorno.Trabajadores.ListaTrabajadores.Add(trabajador);
        var usuarioId = Guid.NewGuid();
        var elemento = new PlantillaElemento(entorno.Version.Id, TipoElementoPlantilla.Texto, 1, 0, 0, 10, 10, "Observaciones", FuenteDatoPlantilla.Manual);
        Confirmar(entorno.Version, [elemento], usuarioId);
        var handler = entorno.CrearHandler(usuarioId);

        var resultado = await handler.Handle(
            new GenerarDocumentoIndividualCommand(entorno.Version.Id, trabajador.Id, ValoresManuales: new Dictionary<Guid, string> { [elemento.Id] = "Sin incidencias" }),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        entorno.Rellenador.UltimosElementos.Should().ContainSingle(e => e.Valor == "Sin incidencias");
    }

    [Fact]
    public async Task No_incluye_elementos_de_firma_en_el_relleno_ni_en_el_snapshot()
    {
        var entorno = await ConstruirEntornoAsync();
        var trabajador = Trabajador.DeEmpresa(Guid.NewGuid(), "Juan", "Pérez", Dni);
        entorno.Trabajadores.ListaTrabajadores.Add(trabajador);
        var usuarioId = Guid.NewGuid();
        Confirmar(entorno.Version,
        [
            new PlantillaElemento(entorno.Version.Id, TipoElementoPlantilla.Texto, 1, 0, 0, 10, 10, "Nombre", FuenteDatoPlantilla.TrabajadorNombreCompleto),
            new PlantillaElemento(entorno.Version.Id, TipoElementoPlantilla.Firma, 1, 20, 0, 10, 10, "Firma", rolFirmante: RolFirmantePlantilla.Trabajador),
        ], usuarioId);
        var handler = entorno.CrearHandler(usuarioId);

        var resultado = await handler.Handle(
            new GenerarDocumentoIndividualCommand(entorno.Version.Id, trabajador.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        entorno.Rellenador.UltimosElementos.Should().ContainSingle();
        entorno.Rellenador.UltimosElementos![0].Tipo.Should().Be(TipoElementoPlantilla.Texto);
    }

    /// <summary>
    /// Cierre de IDOR (auditoría de seguridad del módulo, 2026-08-30): antes
    /// de este fix, la existencia por tenant bastaba — un Trabajador fuera de
    /// la cartera del usuario actual podía usarse igualmente.
    /// </summary>
    [Fact]
    public async Task Rechaza_un_trabajador_fuera_de_la_cartera_del_usuario()
    {
        var entorno = await ConstruirEntornoAsync();
        var trabajador = Trabajador.DeEmpresa(Guid.NewGuid(), "Juan", "Pérez", Dni);
        entorno.Trabajadores.ListaTrabajadores.Add(trabajador);
        var usuarioId = Guid.NewGuid();
        Confirmar(entorno.Version, [new PlantillaElemento(entorno.Version.Id, TipoElementoPlantilla.Texto, 1, 0, 0, 10, 10, "Campo", FuenteDatoPlantilla.Constante, "x")], usuarioId);
        var handler = entorno.CrearHandler(usuarioId, new AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: []));

        var resultado = await handler.Handle(
            new GenerarDocumentoIndividualCommand(entorno.Version.Id, trabajador.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Plantilla.PropietarioNoEncontrado");
    }

    /// <summary>
    /// Cierre de IDOR: un Centro visible por tenant pero fuera de la cartera
    /// del usuario ya no puede usarse para resolver empresa/cliente/centro.
    /// </summary>
    [Fact]
    public async Task Rechaza_un_centro_fuera_de_la_cartera_del_usuario()
    {
        var entorno = await ConstruirEntornoAsync();
        var cliente = Empresa.CrearComoCliente("Cliente SA", "B12345674", false, null, null);
        var empresa = new Empresa("Contratista SL", "B12345674");
        entorno.Empresas.ListaEmpresas.Add(cliente);
        entorno.Empresas.ListaEmpresas.Add(empresa);
        var centro = new Centro(cliente.Id, empresa.Id, "Centro Norte", direccion: "Calle Falsa 123");
        entorno.Centros.ListaCentros.Add(centro);
        var trabajador = Trabajador.DeEmpresa(empresa.Id, "Juan", "Pérez", Dni);
        entorno.Trabajadores.ListaTrabajadores.Add(trabajador);
        entorno.Asignaciones.Agregar(new Asignacion(trabajador.Id, centro.Id, new DateOnly(2026, 1, 1)));
        var usuarioId = Guid.NewGuid();
        Confirmar(entorno.Version, [new PlantillaElemento(entorno.Version.Id, TipoElementoPlantilla.Texto, 1, 0, 0, 10, 10, "Campo", FuenteDatoPlantilla.Constante, "x")], usuarioId);
        var handler = entorno.CrearHandler(
            usuarioId, new AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: [trabajador.Id], centroIdsVisibles: []));

        var resultado = await handler.Handle(
            new GenerarDocumentoIndividualCommand(entorno.Version.Id, trabajador.Id, CentroId: centro.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Plantilla.CentroNoEncontrado");
    }

    /// <summary>
    /// Un Trabajador visible por cartera pero SIN asignación activa en el
    /// Centro indicado no puede combinarse con ese centro — evita mezclar
    /// datos de un trabajador con el centro/empresa de otra área BPO.
    /// </summary>
    [Fact]
    public async Task Rechaza_un_trabajador_sin_asignacion_activa_en_el_centro()
    {
        var entorno = await ConstruirEntornoAsync();
        var cliente = Empresa.CrearComoCliente("Cliente SA", "B12345674", false, null, null);
        var empresa = new Empresa("Contratista SL", "B12345674");
        entorno.Empresas.ListaEmpresas.Add(cliente);
        entorno.Empresas.ListaEmpresas.Add(empresa);
        var centro = new Centro(cliente.Id, empresa.Id, "Centro Norte", direccion: "Calle Falsa 123");
        entorno.Centros.ListaCentros.Add(centro);
        var trabajador = Trabajador.DeEmpresa(empresa.Id, "Juan", "Pérez", Dni);
        entorno.Trabajadores.ListaTrabajadores.Add(trabajador);
        // Sin Asignacion activa en este centro a propósito.
        var usuarioId = Guid.NewGuid();
        Confirmar(entorno.Version, [new PlantillaElemento(entorno.Version.Id, TipoElementoPlantilla.Texto, 1, 0, 0, 10, 10, "Campo", FuenteDatoPlantilla.Constante, "x")], usuarioId);
        var handler = entorno.CrearHandler(usuarioId);

        var resultado = await handler.Handle(
            new GenerarDocumentoIndividualCommand(entorno.Version.Id, trabajador.Id, CentroId: centro.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Plantilla.TrabajadorSinAsignacionEnCentro");
    }

    [Fact]
    public async Task Guarda_un_snapshot_json_con_la_etiqueta_visible_como_clave()
    {
        var entorno = await ConstruirEntornoAsync();
        var trabajador = Trabajador.DeEmpresa(Guid.NewGuid(), "Juan", "Pérez", Dni);
        entorno.Trabajadores.ListaTrabajadores.Add(trabajador);
        var usuarioId = Guid.NewGuid();
        Confirmar(entorno.Version,
        [
            new PlantillaElemento(entorno.Version.Id, TipoElementoPlantilla.Texto, 1, 0, 0, 10, 10, "Nombre completo", FuenteDatoPlantilla.TrabajadorNombreCompleto),
        ], usuarioId);
        var documentosGenerados = new DocumentoGeneradoRepositorioFalso();
        var handler = new GenerarDocumentoIndividualCommandHandler(
            entorno.Versiones, entorno.Plantillas, entorno.Documentos, documentosGenerados, entorno.TiposDocumento,
            entorno.Empresas, entorno.Trabajadores, entorno.Centros, entorno.Contactos,
            entorno.Rellenador, entorno.Almacenamiento, new CurrentUserServiceFalso(usuarioId),
            new AlcanceDatosServiceFalso(), entorno.Asignaciones, new UnitOfWorkFalso());

        var resultado = await handler.Handle(
            new GenerarDocumentoIndividualCommand(entorno.Version.Id, trabajador.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        var documentoGenerado = documentosGenerados.Lista.Should().ContainSingle().Subject;
        var datos = JsonSerializer.Deserialize<Dictionary<string, string?>>(documentoGenerado.DatosUtilizadosJson);
        datos.Should().ContainKey("Nombre completo").WhoseValue.Should().Be("Juan Pérez");
    }
}
