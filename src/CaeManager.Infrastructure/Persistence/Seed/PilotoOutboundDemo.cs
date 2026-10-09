using System.Globalization;
using System.Text;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using Microsoft.Extensions.Configuration;

namespace CaeManager.Infrastructure.Persistence.Seed;

/// <summary>Qué datos recibe un Tenant propietario del piloto.</summary>
public enum EscenarioPilotoOutbound
{
    /// <summary>Tenant, operación, carteras, Empresa propia y su agenda, sin más datos. Hoy ningún Tenant del catálogo lo usa.</summary>
    Esqueleto,

    /// <summary>Volumen y mezcla realista, con todos los casos de estado al menos una vez (ver <see cref="DisenoT1PilotoOutbound"/>).</summary>
    Grande,

    /// <summary>Las seis condiciones de «todo al día» a la vez: cero filas en Mi trabajo y 100 % en todas las pantallas.</summary>
    TodoAlDia,

    /// <summary>50 % en todas las pantallas (ver <see cref="ConstruccionMitadPilotoOutbound"/>).</summary>
    Mitad,

    /// <summary>Alta reciente: requisitos definidos y ningún documento, nunca (ver <see cref="CatalogoPilotoOutbound.CumplimientoDeInicioYVisionDeCarteraEnT4"/>).</summary>
    TodoPendiente,

    /// <summary>Cuatro Trabajadores en catorce Centros: un mismo documento vale, vence o está en tolerancia según el Centro (ver <see cref="DisenoT5PilotoOutbound"/>).</summary>
    PocosTrabajadoresMuchosCentros,

    /// <summary>Tres zonas con trabajo pendiente desigual y un desplazamiento temporal entre dos de ellas (ver <see cref="DisenoT6PilotoOutbound"/>).</summary>
    Territorial
}

/// <summary>
/// Una zona territorial de la Empresa propia. No es un concepto del modelo: es una
/// convención de datos (el nombre y el código del Centro la llevan escrita) y no
/// concede ni restringe alcance a nadie.
/// </summary>
/// <param name="Codigo">Tres letras, para el código del Centro de Trabajo.</param>
public sealed record ZonaPilotoOutbound(string Nombre, string Codigo);

/// <param name="Nombre">Nombre completo del Centro de Trabajo, tal como se ve en pantalla. Con <paramref name="Zona"/>, empieza por el nombre de la zona.</param>
/// <param name="Zona">Solo en el Tenant territorial: la zona a la que pertenece el Centro.</param>
public sealed record CentroPilotoOutbound(string Nombre, string Localidad, ZonaPilotoOutbound? Zona = null);

public sealed record ClienteEmpresarialPilotoOutbound(string RazonSocial, IReadOnlyList<CentroPilotoOutbound> Centros);

/// <summary>Cómo está el documento de un Trabajador cuando no está, sin más, Vigente o sin caducidad.</summary>
public enum SituacionDocumentoPilotoOutbound
{
    Vencido,
    Urgente,
    Proximo,

    /// <summary>El Trabajador no tiene ningún documento de ese tipo: Faltante en cada Centro que lo exige.</summary>
    Ausente,

    /// <summary>El documento existe, no tiene fecha de vencimiento y nadie ha confirmado que no caduca. Solo en tipos sin vencimiento automático.</summary>
    SinConfirmar
}

/// <summary>Un documento que se aparta de «todo en regla»: de quién (por índice en el Tenant), de qué tipo y cómo está.</summary>
public sealed record DesviacionDocumentoPilotoOutbound(int Trabajador, string Tipo, SituacionDocumentoPilotoOutbound Situacion);

/// <param name="Clave">Identificador corto de la matriz (T1..T6): va en los mensajes de la autoverificación y de la siembra, nunca en un dato que se vea en pantalla.</param>
/// <param name="Nombre">Nombre del Tenant propietario y razón social de su Empresa propia.</param>
/// <param name="EnCarteraDelGestorSegundo">La Gestora CAE primera lleva los seis; el segundo Gestor CAE, solo los marcados.</param>
/// <param name="Subcontratas">Razones sociales de las Empresas subcontratistas de la Empresa propia; <c>null</c> = ninguna.</param>
public sealed record TenantPilotoOutbound(
    string Clave, string Nombre, EscenarioPilotoOutbound Escenario, bool EnCarteraDelGestorSegundo,
    int Trabajadores, IReadOnlyList<ClienteEmpresarialPilotoOutbound> ClientesEmpresariales,
    IReadOnlyList<string>? Subcontratas = null)
{
    public IReadOnlyList<CentroPilotoOutbound> Centros { get; } = [.. ClientesEmpresariales.SelectMany(c => c.Centros)];

    /// <summary>Los Centros de una zona, en el orden del catálogo: su posición es el número de su código.</summary>
    public IReadOnlyList<CentroPilotoOutbound> CentrosDe(ZonaPilotoOutbound zona) => [.. Centros.Where(c => c.Zona == zona)];

    /// <summary>
    /// El código del Centro, como lo escribiría la Empresa propia: tres letras y un
    /// número de orden, sin la clave de la matriz. Si el Centro tiene zona, las
    /// letras son las de la zona y el número, su orden dentro de ella («BCN-02»:
    /// buscando «BCN» salen los Centros de esa zona); si no, las tres primeras de
    /// su localidad, y el número cuenta los Centros del Tenant que las comparten
    /// («ZAR-01», «ZAR-02»). El modelo no exige que sea único; aquí no se repite
    /// dentro de un Tenant.
    /// </summary>
    public string CodigoDe(CentroPilotoOutbound centro)
    {
        if (centro.Zona is { } zona)
            return $"{zona.Codigo}-{CentrosDe(zona).ToList().IndexOf(centro) + 1:D2}";

        var letras = IdentidadesPilotoOutbound.LetrasDeLaLocalidad(centro.Localidad);
        var conEsasLetras = Centros.Where(c => c.Zona is null && IdentidadesPilotoOutbound.LetrasDeLaLocalidad(c.Localidad) == letras).ToList();
        return $"{letras}-{conEsasLetras.IndexOf(centro) + 1:D2}";
    }
}

/// <summary>Direcciones de las cuentas de la demostración. Las locales son las del arranque en Development; la vía administrativa pasa las de un dominio propio.</summary>
/// <param name="UsuarioDeClienteEmpresarialT1">La cuenta opcional de la matriz: un Usuario de Cliente empresarial de T1, que solo lee lo de su Cliente empresarial.</param>
public sealed record CuentasPilotoOutbound(
    string GestoraPrimera, string GestorSegundo, string Coordinadora, string AdministradorOperador, string AdministradorT1,
    string UsuarioDeClienteEmpresarialT1)
{
    public static CuentasPilotoOutbound Locales { get; } = new(
        "gestora1.piloto@caemanager.local", "gestor2.piloto@caemanager.local", "coordinadora.piloto@caemanager.local",
        "administrador.piloto@caemanager.local", "administrador.t1.piloto@caemanager.local",
        "cliente.t1.piloto@caemanager.local");

    public IReadOnlyList<string> Todas =>
        [GestoraPrimera, GestorSegundo, Coordinadora, AdministradorOperador, AdministradorT1, UsuarioDeClienteEmpresarialT1];
}

/// <summary>
/// A qué dirección escribe «Pedir» en los Tenants del piloto. Con
/// <see cref="Correo"/>, todos los contactos de agenda llevan una variante
/// «+etiqueta» de esa dirección (mismo buzón, y se ve a qué contacto se escribió);
/// sin él, direcciones no entregables bajo <see cref="Dominio"/>.
/// </summary>
public sealed record ContactosPilotoOutbound(string? Correo, string Dominio)
{
    /// <summary>El dominio de la siembra local: «.local» no se enruta, así que nada sale hacia un tercero.</summary>
    public const string DominioPorDefecto = "caemanager.local";

    public static ContactosPilotoOutbound NoEntregables { get; } = new(null, DominioPorDefecto);

    public string DireccionDe(string etiqueta)
    {
        if (Correo is null)
            return $"{etiqueta}@{Dominio}";

        var arroba = Correo.LastIndexOf('@');
        return $"{Correo[..arroba]}+{etiqueta}{Correo[arroba..]}";
    }

    /// <summary>La regla que la autoverificación exige a cada contacto de agenda de los Tenants del piloto.</summary>
    public bool Cumple(string email)
    {
        if (Correo is null)
            return email.EndsWith("@" + Dominio, StringComparison.OrdinalIgnoreCase);

        var arroba = Correo.LastIndexOf('@');
        return email.StartsWith(Correo[..arroba] + "+", StringComparison.OrdinalIgnoreCase)
               && email.EndsWith(Correo[arroba..], StringComparison.OrdinalIgnoreCase);
    }

    internal static ContactosPilotoOutbound Crear(string? correo, string? dominio)
    {
        correo = string.IsNullOrWhiteSpace(correo) ? null : correo.Trim();
        dominio = string.IsNullOrWhiteSpace(dominio) ? DominioPorDefecto : dominio.Trim().TrimStart('@');

        if (correo is not null)
        {
            var arroba = correo.IndexOf('@');
            if (arroba <= 0 || arroba != correo.LastIndexOf('@') || arroba == correo.Length - 1 || correo.Contains('+') || correo.Contains(' '))
                throw new InvalidOperationException(
                    $"{OpcionesPilotoOutbound.ClaveCorreoContactos} no es una dirección utilizable: tiene que ser una sola " +
                    "dirección «usuario@dominio», sin «+» (la siembra añade «+etiqueta» por contacto).");
        }

        return new ContactosPilotoOutbound(correo, dominio);
    }
}

/// <summary>Lo que el arranque lee de <c>DatosPrueba:PilotoOutbound:*</c>.</summary>
public sealed record OpcionesPilotoOutbound(DateOnly FechaDemostracion, ContactosPilotoOutbound Contactos)
{
    public const string Seccion = "DatosPrueba:PilotoOutbound";
    public const string ClaveActivo = Seccion + ":Activo";
    public const string ClaveFechaDemostracion = Seccion + ":FechaDemostracion";
    public const string ClaveCorreoContactos = Seccion + ":CorreoContactos";
    public const string ClaveDominioContactos = Seccion + ":DominioContactos";

    /// <summary>Días que la demostración puede quedar por delante de hoy: con más, los «Vencido» (hasta D−10) aún no lo estarían.</summary>
    public const int MargenMaximoDias = 9;

    public static bool Activo(IConfiguration configuration) =>
        configuration.GetValue<bool>("DatosPrueba:Activo") && configuration.GetValue<bool>(ClaveActivo);

    /// <summary>
    /// Lee las opciones y valida su forma. Lanza <see cref="InvalidOperationException"/> con el
    /// nombre de la clave: se llama antes de cualquier escritura. Que la fecha quede dentro de
    /// su margen respecto de hoy no se exige aquí sino al sembrar, y solo si hay algo que
    /// escribir (<see cref="MotivoFechaNoUtilizable"/>): un re-arranque al día siguiente de la
    /// demostración, con todo ya sembrado, no tiene por qué caerse.
    /// </summary>
    public static OpcionesPilotoOutbound Leer(IConfiguration configuration)
    {
        var textoFecha = configuration[ClaveFechaDemostracion];
        if (string.IsNullOrWhiteSpace(textoFecha))
            throw new InvalidOperationException(
                $"Falta {ClaveFechaDemostracion}: la siembra del piloto ancla todas las fechas al día de la demostración (formato yyyy-MM-dd).");

        if (!DateOnly.TryParseExact(textoFecha.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fecha))
            throw new InvalidOperationException(
                $"{ClaveFechaDemostracion} = «{textoFecha}» no es una fecha con formato yyyy-MM-dd.");

        return new OpcionesPilotoOutbound(
            fecha, ContactosPilotoOutbound.Crear(configuration[ClaveCorreoContactos], configuration[ClaveDominioContactos]));
    }

    /// <summary>
    /// Por qué no se puede sembrar hoy con esa fecha de demostración, o <c>null</c> si se
    /// puede: tiene que ser hoy o quedar, como mucho, a <see cref="MargenMaximoDias"/> días.
    /// </summary>
    public static string? MotivoFechaNoUtilizable(DateOnly fechaDemostracion, DateOnly hoy)
    {
        if (fechaDemostracion < hoy)
            return $"{ClaveFechaDemostracion} = {fechaDemostracion:yyyy-MM-dd} es anterior a hoy ({hoy:yyyy-MM-dd}): " +
                   "los documentos sembrados como vigentes ya habrían empezado a acercarse a su vencimiento.";

        if (fechaDemostracion > hoy.AddDays(MargenMaximoDias))
            return $"{ClaveFechaDemostracion} = {fechaDemostracion:yyyy-MM-dd} queda a más de {MargenMaximoDias} días de hoy " +
                   $"({hoy:yyyy-MM-dd}): los documentos sembrados como vencidos todavía no lo estarían hoy.";

        return null;
    }

    public static void ValidarFecha(DateOnly fechaDemostracion, DateOnly hoy)
    {
        if (MotivoFechaNoUtilizable(fechaDemostracion, hoy) is { } motivo)
            throw new InvalidOperationException(motivo);
    }
}

/// <summary>
/// Las fechas de la siembra, todas relativas al día de la demostración (D) y nunca
/// al día en que se siembra: ningún documento cambia de estado entre el ensayo y
/// la demostración. Márgenes de la matriz: Vigente, más de 60 días después;
/// Próximo, 20..25; Urgente, 5..10; Vencido, entre 10 y 90 días antes.
///
/// <para>
/// Próximo y Urgente usan solo los dos primeros días de su margen. El estado se
/// calcula contra HOY, y hoy puede ser cualquier día entre
/// D−<see cref="OpcionesPilotoOutbound.MargenMaximoDias"/> y D: con los umbrales por
/// defecto (ámbar 30, rojo 15), un vencimiento en D+22 es todavía Vigente el día
/// D−9, y uno en D+7 es todavía Próximo. D+20..D+21 es Próximo y D+5..D+6 es
/// Urgente los diez días.
/// </para>
///
/// <para>
/// <b>Emisión y vencimiento van juntos, y los decide el Tipo de documento.</b> El
/// producto no deja crear un documento de un Tipo con vencimiento automático cuyo
/// vencimiento no sea su emisión más los meses del Tipo
/// (<see cref="CalculadoraEstadoDocumento.ResolverVigencia"/>), así que la siembra
/// tampoco: <see cref="EnRegla"/>, <see cref="ConVencimiento"/> y
/// <see cref="DesdeEmision"/> devuelven siempre el par, y en esos Tipos una de las
/// dos fechas se calcula desde la otra. Ninguna emisión es posterior a
/// D−<see cref="OpcionesPilotoOutbound.MargenMaximoDias"/>, el primer día en que se
/// puede sembrar: nunca es futura.
/// </para>
/// </summary>
public sealed record FechasPilotoOutbound(DateOnly FechaDemostracion)
{
    // Los umbrales por defecto de un Tenant: «Próximo» a 30 días o menos, «Urgente» a 15 o menos.
    private const int UmbralAmbarDias = 30;
    private const int UmbralRojoDias = 15;

    /// <summary>Lo que se le supone a un documento de fecha manual: se emitió doce meses antes de vencer.</summary>
    private const int MesesDeUnDocumentoDeFechaManual = 12;

    private const int AnosDeUnDocumentoDeIdentidad = 10;

    public DateOnly Vigente(int i) => FechaDemostracion.AddDays(75 + Positivo(i) % 240);
    public DateOnly Proximo(int i) => FechaDemostracion.AddDays(20 + Positivo(i) % 2);
    public DateOnly Urgente(int i) => FechaDemostracion.AddDays(5 + Positivo(i) % 2);
    public DateOnly Vencido(int i) => FechaDemostracion.AddDays(-(10 + Positivo(i) % 81));

    /// <summary>Emisión de un documento que no caduca o que nadie ha confirmado: no hay vencimiento del que calcularla.</summary>
    public DateOnly Emision(int i) => FechaDemostracion.AddDays(-(45 + Positivo(i) % 280));

    /// <summary>El último día en que puede estar emitido un documento sin ser futuro el día en que se siembra, sea cual sea.</summary>
    private DateOnly UltimaEmision => FechaDemostracion.AddDays(-OpcionesPilotoOutbound.MargenMaximoDias);

    /// <summary>
    /// Las fechas de un documento que el diseño no pone en ningún estado a propósito:
    /// «en regla». Lo que eso quiere decir depende del Tipo.
    /// <list type="bullet">
    /// <item>Vencimiento automático de doce meses o más: vence en <see cref="Vigente"/> y
    /// se emitió los meses del Tipo antes.</item>
    /// <item>Vencimiento automático de dos a once meses (RLC, RNT y sus recibos, de
    /// tres): recién emitido —entre D−9 y D−18— y Vigente los diez días.</item>
    /// <item>Vencimiento automático de un mes (el certificado de estar al corriente con
    /// la Seguridad Social y el ITA): a un documento mensual coherente le quedan, como
    /// mucho, 31 días, así que está «Próximo» —nunca «Urgente»— los diez días. Se
    /// emitió en D−9, o en D−10 si desde D−9 vencería en D+22.</item>
    /// <item>Sin vencimiento automático y de
    /// <see cref="CatalogoPilotoOutbound.TiposQueNoCaducan"/>: no caduca.</item>
    /// <item>Documento de identidad: vence a lo largo de los ocho años siguientes y se
    /// emitió diez años antes.</item>
    /// <item>Cualquier otro sin vencimiento automático: fecha manual, en
    /// <see cref="Vigente"/>, emitido doce meses antes.</item>
    /// </list>
    /// </summary>
    /// <param name="semillaDeVencimiento">
    /// La semilla del vencimiento. Sin ella se usa la de la emisión: es el caso de los
    /// documentos que antes se sembraban «no caduca» y pedían al contador de la siembra
    /// una sola fecha.
    /// </param>
    public (DateOnly Emision, DateOnly? Vence) EnRegla(TipoDocumento tipo, int semillaDeEmision, int? semillaDeVencimiento = null)
    {
        var vigente = Vigente(semillaDeVencimiento ?? semillaDeEmision);

        if (tipo.AplicaVencimientoAutomatico)
        {
            return MesesDe(tipo) switch
            {
                >= 12 => ConVencimiento(tipo, vigente),
                > 1 => Reciente(tipo, semillaDeEmision),
                _ => Mensual(tipo)
            };
        }

        if (CatalogoPilotoOutbound.TiposQueNoCaducan.Contains(tipo.Nombre))
            return (Emision(semillaDeEmision), null);

        if (tipo.Nombre == CatalogoPilotoOutbound.DocumentoIdentidad)
        {
            var vence = FechaDemostracion.AddDays(75 + Positivo(semillaDeEmision) % 3000);
            return (NoFutura(tipo, vence.AddYears(-AnosDeUnDocumentoDeIdentidad)), vence);
        }

        return ConVencimiento(tipo, vigente);
    }

    /// <summary>
    /// Las fechas de un documento cuyo vencimiento fija el diseño (Vencido, Urgente,
    /// Próximo, en tolerancia…): el vencimiento es el dado y la emisión se calcula
    /// hacia atrás, con los meses del Tipo si vence solo y con doce si su fecha es manual.
    ///
    /// <para>
    /// Un vencimiento en un día que ninguna emisión alcanza —el 29 de febrero con una
    /// vigencia de años; el 29, 30 o 31 con una de meses, si el mes de la emisión es más
    /// corto— no existe para el producto. Ahí, y solo ahí, el vencimiento retrocede al
    /// día que sí se alcanza: uno en las vigencias de años y tres como mucho en las
    /// demás, que caben en todos los márgenes de arriba y en las tolerancias del diseño.
    /// </para>
    /// </summary>
    public (DateOnly Emision, DateOnly Vence) ConVencimiento(TipoDocumento tipo, DateOnly vencimientoDeDiseno)
    {
        if (!tipo.AplicaVencimientoAutomatico)
            return (NoFutura(tipo, vencimientoDeDiseno.AddMonths(-MesesDeUnDocumentoDeFechaManual)), vencimientoDeDiseno);

        var meses = MesesDe(tipo);
        var emision = vencimientoDeDiseno.AddMonths(-meses);
        var vence = emision.AddMonths(meses);
        if (vencimientoDeDiseno.DayNumber - vence.DayNumber is < 0 or > 3)
            throw new InvalidOperationException(
                $"«{tipo.Nombre}» ({meses} meses): ninguna emisión da un vencimiento a tres días o menos del {vencimientoDeDiseno:yyyy-MM-dd} del diseño.");

        return ComoLoDariaElProducto(tipo, emision, vence);
    }

    /// <summary>
    /// Las fechas de un documento de un Tipo con vencimiento automático del que el
    /// diseño fija la EMISIÓN: el vencimiento es el que el producto le calcularía.
    /// </summary>
    public (DateOnly Emision, DateOnly Vence) DesdeEmision(TipoDocumento tipo, DateOnly emision) =>
        ComoLoDariaElProducto(tipo, emision, emision.AddMonths(MesesDe(tipo)));

    /// <summary>Recién emitido: entre D−9 y D−18, repartido sin azar. Tiene que seguir Vigente el día de la demostración.</summary>
    private (DateOnly Emision, DateOnly Vence) Reciente(TipoDocumento tipo, int semilla)
    {
        var emision = UltimaEmision.AddDays(-(Positivo(semilla) % 10));
        var vence = emision.AddMonths(MesesDe(tipo));
        if (vence.DayNumber - FechaDemostracion.DayNumber <= UmbralAmbarDias)
            throw new InvalidOperationException(
                $"«{tipo.Nombre}» ({MesesDe(tipo)} meses): emitido el {emision:yyyy-MM-dd} vence el {vence:yyyy-MM-dd}, a {UmbralAmbarDias} días o " +
                $"menos de la demostración ({FechaDemostracion:yyyy-MM-dd}), y la siembra lo quería Vigente.");

        return ComoLoDariaElProducto(tipo, emision, vence);
    }

    /// <summary>
    /// La emisión más tardía que no es futura ningún día de siembra y cuyo vencimiento,
    /// un mes después, es «Próximo» de D−9 a D: a más de quince días de D y a treinta o
    /// menos de D−9, es decir, entre D+16 y D+21. Desde D−9 vence entre D+19 y D+22,
    /// según lo que dure el mes; desde D−10, entre D+18 y D+21.
    /// </summary>
    private (DateOnly Emision, DateOnly Vence) Mensual(TipoDocumento tipo)
    {
        for (var atras = 0; atras <= 1; atras++)
        {
            var emision = UltimaEmision.AddDays(-atras);
            var vence = emision.AddMonths(MesesDe(tipo));
            var dias = vence.DayNumber - FechaDemostracion.DayNumber;
            if (dias > UmbralRojoDias && dias + OpcionesPilotoOutbound.MargenMaximoDias <= UmbralAmbarDias)
                return ComoLoDariaElProducto(tipo, emision, vence);
        }

        throw new InvalidOperationException(
            $"«{tipo.Nombre}»: ninguna emisión de D−{OpcionesPilotoOutbound.MargenMaximoDias} o del día anterior da un vencimiento «Próximo» " +
            $"todos los días entre el primero de siembra y la demostración ({FechaDemostracion:yyyy-MM-dd}).");
    }

    /// <summary>
    /// Lo que no se supone: que el par es el que el producto calcularía
    /// (<see cref="CalculadoraEstadoDocumento.CalcularFechaVencimiento"/>) y que la
    /// emisión no es futura el primer día en que se puede sembrar.
    /// </summary>
    private (DateOnly Emision, DateOnly Vence) ComoLoDariaElProducto(TipoDocumento tipo, DateOnly emision, DateOnly vence)
    {
        if (CalculadoraEstadoDocumento.CalcularFechaVencimiento(emision, tipo.VigenciaMeses) != vence)
            throw new InvalidOperationException(
                $"«{tipo.Nombre}»: emitido el {emision:yyyy-MM-dd}, el producto no lo haría vencer el {vence:yyyy-MM-dd}.");

        return (NoFutura(tipo, emision), vence);
    }

    private DateOnly NoFutura(TipoDocumento tipo, DateOnly emision) =>
        emision <= UltimaEmision
            ? emision
            : throw new InvalidOperationException(
                $"«{tipo.Nombre}»: la emisión ({emision:yyyy-MM-dd}) sería posterior a {UltimaEmision:yyyy-MM-dd}, el primer día en que se " +
                "puede sembrar: el documento nacería emitido en el futuro.");

    private static int MesesDe(TipoDocumento tipo) =>
        tipo.FijaVigenciaDesdeLaEmision
            ? tipo.VigenciaMeses!.Value
            : throw new InvalidOperationException(
                $"«{tipo.Nombre}» no vence solo, o dice que sí y no dice en cuántos meses: la siembra no sabe fecharlo.");

    private static int Positivo(int i) => i < 0 ? -i : i;
}

/// <summary>
/// La construcción del Tenant «mitad», que da 50 % en Inicio, Visión de cartera,
/// el listado de Centros, cada Centro y Empresas a la vez, sin que ninguna fila
/// atribuya a un Centro un documento que ese Centro no exige.
///
/// <para>
/// Cada Trabajador tiene UNA sola Asignación activa (<see cref="TrabajadoresPorCentro"/>)
/// y cada Centro exige exactamente {<see cref="TipoVigente"/>, <see cref="TipoVencido"/>}.
/// Cada Trabajador tiene los dos documentos, el primero Vigente y el segundo
/// Vencido, y ninguno de otro tipo: la mitad de los pares exigidos y la mitad de
/// los documentos existentes son conformes, y no falta ninguno.
/// </para>
///
/// <para>
/// Un Centro por Trabajador a propósito: el listado de Centros añade como causa
/// de cada Centro todo documento no vigente de un Trabajador con Asignación activa
/// en él, lo exija o no ese Centro (<c>CalculoEstadoCentroService</c>). Con un
/// Trabajador en dos Centros que exigen cosas distintas, uno de ellos enseñaría
/// un vencido que no pide.
/// </para>
///
/// <para>
/// <see cref="CentroConVisita"/> tiene una Visita con todos sus Trabajadores, a
/// <see cref="DiasHastaLaVisita"/> días de la demostración: es con lo que se
/// mide la comprobación previa de la pantalla de Visitas.
/// </para>
/// </summary>
public static class ConstruccionMitadPilotoOutbound
{
    public const string TipoVigente = CatalogoPilotoOutbound.AptitudMedica;
    public const string TipoVencido = CatalogoPilotoOutbound.FormacionArt19;

    /// <summary>Cuántos Trabajadores tiene cada Centro, en el orden del catálogo: 3 + 3 + 2 + 2 = 10.</summary>
    public static IReadOnlyList<int> TrabajadoresPorCentro { get; } = [3, 3, 2, 2];

    public const int CentroConVisita = 0;
    public const int DiasHastaLaVisita = 15;
}

/// <summary>
/// El diseño de T5, «pocos Trabajadores, muchos Centros»: cuatro Trabajadores,
/// cada uno con Asignación activa a entre ocho y catorce de los catorce Centros
/// (<see cref="CentrosPorTrabajador"/>: el Trabajador n está en los n primeros).
/// Todos los Centros exigen los cinco tipos por defecto, así que veinte documentos
/// responden de 44 × 5 = 220 pares exigidos.
///
/// <para>
/// <b>El mismo documento, tres resultados según el Centro.</b> El certificado de
/// aptitud médica de los Trabajadores de <see cref="TrabajadoresConAptitudAntigua"/>
/// se emitió hace seis meses y veinte días y vence, por su propia fecha, al año de
/// emitirse: es Vigente. Tres Centros lo marcan como requisito que bloquea el
/// acceso, cada uno con sus condiciones (<c>TipoDocumentoCentro</c>):
/// <see cref="CentroSinCondicionesPropias"/> no pone ninguna, y allí vale;
/// <see cref="CentroConPeriodicidadEspecial"/> lo quiere renovado cada
/// <see cref="MesesDePeriodicidadEspecial"/> meses, y allí venció hace unos veinte
/// días: el Trabajador está bloqueado en ese Centro;
/// <see cref="CentroConPeriodicidadEspecialYTolerancia"/> pide la misma renovación
/// pero concede <see cref="DiasDeTolerancia"/> días, y allí vale todavía, en
/// tolerancia. Los otros dos Trabajadores lo tienen emitido hace sesenta días y
/// les vale en los tres.
/// </para>
///
/// <para>
/// <b>Un vencido que un Centro todavía admite.</b> La Formación Art. 19 de
/// <see cref="TrabajadorConElVencido"/> venció hace <see cref="DiasDesdeQueVencio"/>
/// días. Es Vencido en todos sus Centros menos en
/// <see cref="CentroConToleranciaParaElVencido"/>, que concede
/// <see cref="DiasDeToleranciaParaElVencido"/> días y lo pinta «en tolerancia».
/// Ese Centro no lo marca como bloqueante: la tolerancia solo cambia el rótulo.
/// </para>
/// </summary>
public static class DisenoT5PilotoOutbound
{
    /// <summary>A cuántos Centros (los primeros del catálogo) tiene Asignación activa cada Trabajador.</summary>
    public static IReadOnlyList<int> CentrosPorTrabajador { get; } = [14, 12, 10, 8];

    public const string TipoConCondicionesPorCentro = CatalogoPilotoOutbound.AptitudMedica;
    public const int MesesDePeriodicidadEspecial = 6;
    public const int DiasDeTolerancia = 45;

    /// <summary>Días, antes de D, en que venció por la periodicidad especial (con un margen de tres por el calendario).</summary>
    public const int DiasDesdeElVencimientoPorPeriodicidad = 20;

    public const int CentroSinCondicionesPropias = 0;
    public const int CentroConPeriodicidadEspecial = 3;
    public const int CentroConPeriodicidadEspecialYTolerancia = 6;

    public static IReadOnlyList<int> TrabajadoresConAptitudAntigua { get; } = [0, 1];

    public const string TipoVencidoEnTolerancia = CatalogoPilotoOutbound.FormacionArt19;
    public const int TrabajadorConElVencido = 3;
    public const int DiasDesdeQueVencio = 12;
    public const int CentroConToleranciaParaElVencido = 7;
    public const int DiasDeToleranciaParaElVencido = 30;

    /// <summary>Además de lo anterior: un documento Urgente, para que los Centros de T5 no sean solo rojos y verdes.</summary>
    public static IReadOnlyList<DesviacionDocumentoPilotoOutbound> Desviaciones { get; } =
        [new(2, CatalogoPilotoOutbound.EntregaEpi, SituacionDocumentoPilotoOutbound.Urgente)];
}

/// <summary>
/// El diseño de T6, «territorial», con solo datos: veinticuatro Trabajadores, ocho
/// por zona (los índices 0..7 son de Barcelona, 8..15 de Madrid y 16..23 de
/// Santander), y doce Centros, cuatro por zona, con la zona en el nombre y en el
/// código. Ni el Trabajador ni el Centro tienen un campo de zona.
///
/// <para>
/// Dentro de su zona, el Trabajador k (0..7) tiene Asignación activa al Centro
/// k/2, y los dos que son múltiplo de cuatro, además, al siguiente: diez
/// Asignaciones activas por zona, treinta en total, más la del desplazamiento.
/// </para>
///
/// <para>
/// El trabajo pendiente es desigual (<see cref="Desviaciones"/>): Barcelona es la
/// peor, Madrid queda en medio y Santander está casi limpia.
/// <see cref="CentroConRequisitoBloqueante"/> marca el certificado de aptitud
/// médica como requisito que bloquea el acceso, y a un Trabajador asignado allí le
/// falta: es el Faltante bloqueante. Los demás documentos ausentes son Faltantes
/// que no bloquean.
/// </para>
///
/// <para>
/// <see cref="TrabajadorDesplazado"/>, de Madrid, tiene además una Asignación
/// activa, de alta reciente, a <see cref="CentroDelDesplazamiento"/>, de
/// Barcelona. Es el mismo Trabajador, no una copia. La Asignación no lleva fecha
/// de baja porque el modelo solo considera activa la que no la tiene (una baja,
/// aunque sea futura, la deja inerte y el Trabajador no aparecería en ese
/// Centro): lo temporal del desplazamiento lo dice una Visita con fechas en ese
/// Centro, que le incluye, a más de 48 horas de la demostración.
/// </para>
///
/// <para>
/// Ese Centro pide, además de los cinco tipos por defecto,
/// <see cref="TipoPropioDelCentroDelDesplazamiento"/>, que los Centros de Madrid
/// no piden. Los dos Trabajadores de Barcelona asignados allí lo tienen; al
/// desplazado le falta: es la coordinación entre zonas. Con él, el Centro tiene
/// 3 × 6 = 18 pares exigidos y el Tenant, 158.
/// </para>
/// </summary>
public static class DisenoT6PilotoOutbound
{
    public static ZonaPilotoOutbound Barcelona { get; } = new("Barcelona", "BCN");
    public static ZonaPilotoOutbound Madrid { get; } = new("Madrid", "MAD");
    public static ZonaPilotoOutbound Santander { get; } = new("Santander", "SDR");

    public static IReadOnlyList<ZonaPilotoOutbound> Zonas { get; } = [Barcelona, Madrid, Santander];

    public const int TrabajadoresPorZona = 8;

    /// <summary>El cargo del contacto interno de cada zona, en la agenda de la Empresa propia.</summary>
    public static string CargoDeCoordinacion(ZonaPilotoOutbound zona) => $"Coordinación documental — {zona.Nombre}";

    /// <summary>(Trabajador por índice en el Tenant, zona, posición del Centro dentro de la zona).</summary>
    public static IReadOnlyList<(int Trabajador, ZonaPilotoOutbound Zona, int Centro)> AsignacionesActivas { get; } =
    [
        .. from z in Enumerable.Range(0, 3)
           from k in Enumerable.Range(0, TrabajadoresPorZona)
           from centro in k % 4 == 0 ? new[] { k / 2, (k / 2 + 1) % 4 } : new[] { k / 2 }
           select (z * TrabajadoresPorZona + k, Zonas[z], centro)
    ];

    public const string TipoBloqueante = CatalogoPilotoOutbound.AptitudMedica;

    /// <summary>El tercer Centro de Barcelona: allí están los Trabajadores 4 y 5, y al 5 le falta el certificado.</summary>
    public static (ZonaPilotoOutbound Zona, int Centro) CentroConRequisitoBloqueante { get; } = (Barcelona, 2);

    public const int TrabajadorDesplazado = 8;
    public static (ZonaPilotoOutbound Zona, int Centro) CentroDelDesplazamiento { get; } = (Barcelona, 0);
    public const int DiasDelAltaAntesDeLaDemostracion = 10;
    public const string TipoPropioDelCentroDelDesplazamiento = CatalogoPilotoOutbound.CarretillasElevadoras;

    /// <summary>La Visita del desplazamiento va de D+10 a D+20: ningún día entre D−9 y D queda a menos de diez días de su inicio.</summary>
    public const int DiasHastaElInicioDeLaVisita = 10;
    public const int DiasHastaElFinDeLaVisita = 20;

    /// <summary>Lo que cada subcontrata tiene de su propia Empresa, todo Vigente.</summary>
    public static IReadOnlyList<string> TiposDeDocumentoDeSubcontrata { get; } =
        ["Seguro de Responsabilidad Civil + recibo de pago", "Evaluación de Riesgos Laborales", "Modalidad Preventiva"];

    public static IReadOnlyList<DesviacionDocumentoPilotoOutbound> Desviaciones { get; } =
    [
        // Barcelona (0..7): cuatro vencidos, tres ausentes —uno de ellos bloqueante— y un urgente.
        new(0, CatalogoPilotoOutbound.FormacionArt19, SituacionDocumentoPilotoOutbound.Vencido),
        new(1, CatalogoPilotoOutbound.AptitudMedica, SituacionDocumentoPilotoOutbound.Vencido),
        new(2, CatalogoPilotoOutbound.EntregaEpi, SituacionDocumentoPilotoOutbound.Vencido),
        new(3, CatalogoPilotoOutbound.FormacionArt19, SituacionDocumentoPilotoOutbound.Vencido),
        new(4, CatalogoPilotoOutbound.DocumentoIdentidad, SituacionDocumentoPilotoOutbound.Ausente),
        new(5, CatalogoPilotoOutbound.AptitudMedica, SituacionDocumentoPilotoOutbound.Ausente),
        new(6, CatalogoPilotoOutbound.InformacionArt18, SituacionDocumentoPilotoOutbound.Ausente),
        new(7, CatalogoPilotoOutbound.EntregaEpi, SituacionDocumentoPilotoOutbound.Urgente),

        // Madrid (8..15): un vencido, dos ausentes, dos urgentes y un próximo.
        new(9, CatalogoPilotoOutbound.AptitudMedica, SituacionDocumentoPilotoOutbound.Vencido),
        new(10, CatalogoPilotoOutbound.AptitudMedica, SituacionDocumentoPilotoOutbound.Proximo),
        new(11, CatalogoPilotoOutbound.EntregaEpi, SituacionDocumentoPilotoOutbound.Urgente),
        new(13, CatalogoPilotoOutbound.FormacionArt19, SituacionDocumentoPilotoOutbound.Urgente),
        new(14, CatalogoPilotoOutbound.EntregaEpi, SituacionDocumentoPilotoOutbound.Ausente),
        new(15, CatalogoPilotoOutbound.InformacionArt18, SituacionDocumentoPilotoOutbound.Ausente),

        // Santander (16..23): un próximo y un urgente.
        new(19, CatalogoPilotoOutbound.EntregaEpi, SituacionDocumentoPilotoOutbound.Proximo),
        new(21, CatalogoPilotoOutbound.AptitudMedica, SituacionDocumentoPilotoOutbound.Urgente)
    ];
}

/// <summary>
/// El diseño de T1, «grande», con reglas aritméticas sobre el índice del
/// Trabajador y sin azar: 175 Trabajadores (los índices 0..149 son de la Empresa
/// propia y 150..174 de las tres subcontratas, a razón de nueve, ocho y ocho) y
/// veintiocho Centros de doce Clientes empresariales.
///
/// <para>
/// <b>Asignaciones.</b> El Trabajador propio i tiene Asignación activa al Centro
/// i % 28 y, si su posición en el ciclo (i % <see cref="Ciclo"/>) es múltiplo de
/// seis, también al que queda catorce más allá. Los seis últimos propios
/// (<see cref="PrimerTrabajadorDeBaja"/>..149) están de baja: su única Asignación
/// tiene fecha de baja, así que no cuentan en ningún Centro, pero sus documentos
/// siguen existiendo. El Trabajador de subcontrata s (0..24) está en el Centro
/// (s + 4) % 28. Asignaciones activas: 144 + 24 + 25 = 193, y con los cinco tipos
/// exigidos por defecto en todos los Centros, 965 pares exigidos (840 de la
/// Empresa propia).
/// </para>
///
/// <para>
/// <b>Documentos.</b> Cada Trabajador tiene los cinco tipos exigidos, Vigentes o
/// sin caducidad, salvo lo que diga <see cref="DesviacionesDe"/>: un ciclo de 24
/// posiciones para los propios (144 = 6 × 24, así que cada posición se repite seis
/// veces entre los que tienen Asignación activa) y una regla sobre s para los de
/// subcontrata. Por ciclo: cinco documentos Vencidos (siete pares, porque dos son
/// de Trabajadores con dos Centros), tres ausentes y nueve sin confirmar (doce
/// pares). No conformes en la Empresa propia: 22 × 6 = 132 de 840 pares, el 84 %.
/// Faltan 18 + 3 = 21 documentos, así que hay 854 de Trabajador; con los 14 de la
/// Empresa propia, los 8 de los dos Vehículos y los 9 de las subcontratas, 885.
/// </para>
///
/// <para>
/// <b>Los casos de estado, cada uno en su sitio.</b>
/// <see cref="CentroConRequisitoBloqueanteVencido"/> marca el certificado de
/// aptitud médica como requisito que bloquea el acceso: los Trabajadores de
/// <see cref="TrabajadoresBloqueadosEnUnCentroYNoEnElOtro"/> lo tienen vencido y
/// quedan bloqueados allí, y no en su otro Centro, que no lo marca.
/// <see cref="CentroConRequisitoBloqueanteAusente"/> marca el documento de
/// identidad, que a <see cref="TrabajadorConFaltanteBloqueante"/> le falta: es el
/// Faltante bloqueante; los otros veinte ausentes no bloquean.
/// <see cref="CentroConTolerancia"/> concede <see cref="DiasDeTolerancia"/> días a
/// la Formación Art. 19, y la de <see cref="TrabajadorEnTolerancia"/> venció hace
/// <see cref="DiasDesdeQueVencioElDocumentoEnTolerancia"/>: allí se pinta «en
/// tolerancia». Seis Centros (<see cref="CentrosConPlataforma"/>) tienen canal de
/// plataforma y una acreditación externa por documento de cada Trabajador
/// asignado, casi todas Aceptadas: una de cada ocho queda Pendiente de subir,
/// otra de cada ocho Subida, una está Rechazada
/// (<see cref="AcreditacionRechazada"/>) y otra, Aceptada, venció en la plataforma
/// (<see cref="AcreditacionVencidaEnPlataforma"/>): esas dos bloquean su Centro.
/// </para>
///
/// <para>
/// <b>La Visita por correo.</b> <see cref="CentroPorCorreo"/> no tiene plataforma:
/// su canal principal es una dirección de correo. Tiene una Visita que empieza
/// <see cref="DiasHastaLaVisita"/> día después de la demostración, con los
/// Trabajadores de <see cref="TrabajadoresDeLaVisita"/>, que no tienen ninguna
/// desviación: el paquete documental lleva sus diez documentos y los trece que se
/// exigen a la Empresa propia. Es el ÚNICO dato de T1 que depende del día: la
/// Visita es «a menos de 48 horas» la víspera y el día de la demostración, y
/// esos dos días añade una fila a Mi trabajo y una a las Visitas urgentes de
/// Inicio.
/// </para>
///
/// <para>
/// <b>Consecuencia que conviene saber.</b> El documento de Empresa vencido de la
/// Empresa propia (<see cref="TipoDeEmpresaVencido"/>, que no se exige por
/// defecto) es causa de estado en TODOS sus Centros, porque el producto pinta
/// cualquier documento con fecha de la Empresa del Centro: ningún Centro de T1
/// sale mejor que «Vencido».
/// </para>
/// </summary>
public static class DisenoT1PilotoOutbound
{
    public const int TrabajadoresPropios = 150;
    public const int TrabajadoresDeSubcontrata = 25;
    public const int Trabajadores = TrabajadoresPropios + TrabajadoresDeSubcontrata;
    public const int Centros = 28;
    public const int Ciclo = 24;
    public const int PrimerTrabajadorDeBaja = 144;

    public const string TipoBloqueanteVencido = CatalogoPilotoOutbound.AptitudMedica;
    public const int CentroConRequisitoBloqueanteVencido = 0;
    public static IReadOnlyList<int> TrabajadoresBloqueadosEnUnCentroYNoEnElOtro { get; } = [0, 84];

    public const string TipoBloqueanteAusente = CatalogoPilotoOutbound.DocumentoIdentidad;
    public const int CentroConRequisitoBloqueanteAusente = 7;
    public const int TrabajadorConFaltanteBloqueante = 7;

    public const string TipoEnTolerancia = CatalogoPilotoOutbound.FormacionArt19;
    public const int CentroConTolerancia = 5;
    public const int TrabajadorEnTolerancia = 5;
    public const int DiasDesdeQueVencioElDocumentoEnTolerancia = 12;
    public const int DiasDeTolerancia = 30;

    public static IReadOnlyList<int> CentrosConPlataforma { get; } = [0, 3, 6, 9, 16, 20];

    /// <summary>De cada ocho acreditaciones, en el orden en que se siembran, la cuarta queda Pendiente de subir y la séptima Subida.</summary>
    public const int CadaCuantasAcreditaciones = 8;
    public const int PosicionPendienteDeSubir = 3;
    public const int PosicionSubida = 6;

    public static (int Centro, int Trabajador, string Tipo) AcreditacionRechazada { get; } = (3, 3, CatalogoPilotoOutbound.AptitudMedica);
    public static (int Centro, int Trabajador, string Tipo) AcreditacionVencidaEnPlataforma { get; } = (9, 37, CatalogoPilotoOutbound.FormacionArt19);
    public const int DiasDesdeQueVencioEnPlataforma = 15;
    public const int DiasDesdeElRechazo = 12;

    public const int CentroPorCorreo = 13;
    public static IReadOnlyList<int> TrabajadoresDeLaVisita { get; } = [13, 41];
    public const int DiasHastaLaVisita = 1;

    /// <summary>Los dos documentos de la Empresa propia que llevan el PDF pesado.</summary>
    public static IReadOnlyList<string> TiposDeEmpresaConPdfPesado { get; } = ["Evaluación de Riesgos Laborales", "Plan de Prevención"];

    /// <summary>Un tipo de ámbito Empresa que no se exige por defecto: su documento, Vencido, no entra en lo que un Centro pide para una Visita.</summary>
    public const string TipoDeEmpresaVencido = "Mutua";

    public static IReadOnlyList<(string Nombre, string Modelo, string Placa)> Vehiculos { get; } =
        [("Camión grúa", "Camión con grúa autocargante", "7354LDM"), ("Furgoneta de mantenimiento", "Furgoneta de carga ligera", "2196KXV")];

    /// <summary>El documento del primer Vehículo que está Vencido.</summary>
    public const string TipoDeVehiculoVencido = "Seguro";

    /// <summary>Una Gestión pendiente por cada uno: el Gestor CAE tiene apuntado que hay que conseguir ese documento, que falta, para el Centro del Trabajador.</summary>
    public static IReadOnlyList<(int Trabajador, string Tipo)> GestionesPendientes { get; } =
    [
        (31, CatalogoPilotoOutbound.DocumentoIdentidad), (14, CatalogoPilotoOutbound.InformacionArt18),
        (19, CatalogoPilotoOutbound.EntregaEpi), (38, CatalogoPilotoOutbound.InformacionArt18)
    ];

    /// <summary>
    /// La reclamación a la Empresa propia por su documento vencido es un registro
    /// histórico: «se envió» veinte días antes de la demostración. Con veinte, lleva
    /// más de siete días sin respuesta cualquier día en que se pueda sembrar (el
    /// primero es D−9).
    /// </summary>
    public const int DiasDesdeLaReclamacion = 20;

    public static bool EsDeSubcontrata(int trabajador) => trabajador >= TrabajadoresPropios;

    /// <summary>A cuál de las tres subcontratas pertenece (0..2). Solo para los índices 150..174.</summary>
    public static int SubcontrataDe(int trabajador) => (trabajador - TrabajadoresPropios) % 3;

    public static bool EstaDeBaja(int trabajador) => trabajador is >= PrimerTrabajadorDeBaja and < TrabajadoresPropios;

    /// <summary>El Centro de su Asignación principal: activa, o dada de baja si el Trabajador está de baja.</summary>
    public static int CentroPrincipalDe(int trabajador) =>
        EsDeSubcontrata(trabajador) ? (trabajador - TrabajadoresPropios + 4) % Centros : trabajador % Centros;

    /// <summary>Los Centros en los que tiene Asignación activa: ninguno, uno o dos.</summary>
    public static IReadOnlyList<int> CentrosActivosDe(int trabajador)
    {
        if (EstaDeBaja(trabajador))
            return [];

        var principal = CentroPrincipalDe(trabajador);
        return !EsDeSubcontrata(trabajador) && trabajador % Ciclo % 6 == 0 ? [principal, (principal + 14) % Centros] : [principal];
    }

    /// <summary>Los documentos del Trabajador que no están, sin más, Vigentes o sin caducidad.</summary>
    public static IReadOnlyList<DesviacionDocumentoPilotoOutbound> DesviacionesDe(int trabajador)
    {
        IEnumerable<(string Tipo, SituacionDocumentoPilotoOutbound Situacion)> desviaciones;

        if (EsDeSubcontrata(trabajador))
        {
            var s = trabajador - TrabajadoresPropios;
            desviaciones = new (bool Aplica, string Tipo, SituacionDocumentoPilotoOutbound Situacion)[]
                {
                    (s % 5 == 1, CatalogoPilotoOutbound.AptitudMedica, SituacionDocumentoPilotoOutbound.Vencido),
                    (s % 5 == 3, CatalogoPilotoOutbound.InformacionArt18, SituacionDocumentoPilotoOutbound.SinConfirmar),
                    (s % 10 == 4, CatalogoPilotoOutbound.EntregaEpi, SituacionDocumentoPilotoOutbound.Ausente),
                    (s % 10 == 9, CatalogoPilotoOutbound.FormacionArt19, SituacionDocumentoPilotoOutbound.Urgente)
                }
                .Where(d => d.Aplica).Select(d => (d.Tipo, d.Situacion));
        }
        else
        {
            desviaciones = PorPosicionEnElCiclo.GetValueOrDefault(trabajador % Ciclo) ?? [];
        }

        return [.. desviaciones.Select(d => new DesviacionDocumentoPilotoOutbound(trabajador, d.Tipo, d.Situacion))];
    }

    /// <summary>
    /// El ciclo de los Trabajadores propios. Las posiciones 0, 6, 12 y 18 son las de
    /// los que están en dos Centros. Las que no aparecen (2, 8, 13, 17 y 20) no
    /// tienen ninguna desviación.
    /// </summary>
    private static readonly Dictionary<int, (string Tipo, SituacionDocumentoPilotoOutbound Situacion)[]> PorPosicionEnElCiclo = new()
    {
        [0] = [(CatalogoPilotoOutbound.AptitudMedica, SituacionDocumentoPilotoOutbound.Vencido)],
        [1] = [(CatalogoPilotoOutbound.InformacionArt18, SituacionDocumentoPilotoOutbound.SinConfirmar)],
        [3] = [(CatalogoPilotoOutbound.EntregaEpi, SituacionDocumentoPilotoOutbound.Urgente)],
        [4] = [(CatalogoPilotoOutbound.DocumentoIdentidad, SituacionDocumentoPilotoOutbound.SinConfirmar)],
        [5] = [(CatalogoPilotoOutbound.FormacionArt19, SituacionDocumentoPilotoOutbound.Vencido)],
        [6] = [(CatalogoPilotoOutbound.InformacionArt18, SituacionDocumentoPilotoOutbound.SinConfirmar)],
        [7] = [(CatalogoPilotoOutbound.DocumentoIdentidad, SituacionDocumentoPilotoOutbound.Ausente)],
        [9] = [(CatalogoPilotoOutbound.AptitudMedica, SituacionDocumentoPilotoOutbound.Proximo)],
        [10] = [(CatalogoPilotoOutbound.EntregaEpi, SituacionDocumentoPilotoOutbound.Vencido)],
        [11] = [(CatalogoPilotoOutbound.InformacionArt18, SituacionDocumentoPilotoOutbound.SinConfirmar)],
        [12] =
        [
            (CatalogoPilotoOutbound.AptitudMedica, SituacionDocumentoPilotoOutbound.Vencido),
            (CatalogoPilotoOutbound.DocumentoIdentidad, SituacionDocumentoPilotoOutbound.SinConfirmar)
        ],
        [14] = [(CatalogoPilotoOutbound.InformacionArt18, SituacionDocumentoPilotoOutbound.Ausente)],
        [15] = [(CatalogoPilotoOutbound.FormacionArt19, SituacionDocumentoPilotoOutbound.Urgente)],
        [16] = [(CatalogoPilotoOutbound.InformacionArt18, SituacionDocumentoPilotoOutbound.SinConfirmar)],
        [18] = [(CatalogoPilotoOutbound.DocumentoIdentidad, SituacionDocumentoPilotoOutbound.SinConfirmar)],
        [19] = [(CatalogoPilotoOutbound.EntregaEpi, SituacionDocumentoPilotoOutbound.Ausente)],
        [21] =
        [
            (CatalogoPilotoOutbound.InformacionArt18, SituacionDocumentoPilotoOutbound.SinConfirmar),
            (CatalogoPilotoOutbound.FormacionArt19, SituacionDocumentoPilotoOutbound.Proximo)
        ],
        [22] = [(CatalogoPilotoOutbound.AptitudMedica, SituacionDocumentoPilotoOutbound.Vencido)],
        [23] = [(CatalogoPilotoOutbound.DocumentoIdentidad, SituacionDocumentoPilotoOutbound.SinConfirmar)]
    };
}

/// <summary>
/// Los casos de estado de la matriz (§ 3.1) que la autoverificación busca en el
/// Tenant grande. De cada uno tiene que haber al menos uno.
/// </summary>
public static class CasosDeEstadoPilotoOutbound
{
    public const string Vencido = "Documento Vencido";
    public const string Urgente = "Documento Urgente";
    public const string Proximo = "Documento Próximo";
    public const string SinConfirmar = "Documento Sin confirmar";
    public const string SinCaducidad = "Documento Sin caducidad";
    public const string EnTolerancia = "Documento En tolerancia";
    public const string FaltanteBloqueante = "Faltante bloqueante";
    public const string FaltanteNoBloqueante = "Faltante no bloqueante";
    public const string AcreditacionPendienteDeSubir = "Acreditación externa Pendiente de subir";
    public const string AcreditacionSubida = "Acreditación externa Subida";
    public const string AcreditacionAceptada = "Acreditación externa Aceptada";
    public const string AcreditacionRechazada = "Acreditación externa Rechazada";
    public const string AcreditacionVencidaEnPlataforma = "Acreditación externa vencida en plataforma";
    public const string TrabajadorBloqueadoEnUnCentroYNoEnOtro = "Trabajador bloqueado en un Centro y no en otro";
    public const string TrabajadorDeBaja = "Trabajador de baja";
    public const string DocumentoDeEmpresaVencido = "Documento de Empresa vencido en la Empresa propia";
    public const string SubcontrataConDocumentacionPropia = "Subcontrata con documentación propia";
    public const string VehiculoConDocumentoVencido = "Vehículo con documento vencido";
    public const string VisitaAMenosDe48Horas = "Visita a menos de 48 horas de la demostración";
    public const string GestionPendiente = "Gestión pendiente";
    public const string ReclamacionSinRespuesta = "Reclamación enviada sin respuesta";

    public static IReadOnlyList<string> Todos { get; } =
    [
        Vencido, Urgente, Proximo, SinConfirmar, SinCaducidad, EnTolerancia, FaltanteBloqueante, FaltanteNoBloqueante,
        AcreditacionPendienteDeSubir, AcreditacionSubida, AcreditacionAceptada, AcreditacionRechazada, AcreditacionVencidaEnPlataforma,
        TrabajadorBloqueadoEnUnCentroYNoEnOtro, TrabajadorDeBaja, DocumentoDeEmpresaVencido, SubcontrataConDocumentacionPropia,
        VehiculoConDocumentoVencido, VisitaAMenosDe48Horas, GestionPendiente, ReclamacionSinRespuesta
    ];
}

/// <summary>Un intervalo cerrado: lo que la autoverificación admite donde el diseño no fija un número exacto.</summary>
public sealed record IntervaloPilotoOutbound(int Minimo, int Maximo)
{
    public bool Contiene(int? valor) => valor is { } v && v >= Minimo && v <= Maximo;

    public override string ToString() => $"entre {Minimo} y {Maximo}";
}

/// <summary>
/// Lo que la autoverificación exige del Tenant grande: su estructura, con cifras
/// exactas; sus porcentajes y su volumen de trabajo, por intervalo; los casos de
/// estado de la matriz, al menos uno de cada; y el paquete documental de la Visita
/// del Centro que se gestiona por correo, fichero a fichero contra lo exigido.
/// </summary>
/// <param name="DiasHastaLaVisita">La Visita por correo empieza estos días después de la demostración.</param>
public sealed record EsperadoGrandePilotoOutbound(
    int TrabajadoresPropios, int TrabajadoresDeSubcontrata, int Subcontratas, int ClientesEmpresariales, int Centros,
    int Documentos, int ParesExigidos, int ParesFaltantes,
    IntervaloPilotoOutbound CumplimientoEmpresa, IntervaloPilotoOutbound CumplimientoInicioYVisionDeCartera,
    IntervaloPilotoOutbound FilasMiTrabajo, IReadOnlyList<string> CasosDeEstado,
    int DiasHastaLaVisita, int TrabajadoresDeLaVisita, int ExigidosDeTrabajadorEnElPaquete, int ExigidosDeEmpresaEnElPaquete);

/// <summary>Lo que la autoverificación exige de un Centro: su semáforo y su porcentaje.</summary>
public sealed record EsperadoCentroPilotoOutbound(string Centro, EstadoCentro Estado, int Cumplimiento);

/// <summary>
/// Los valores de la matriz para un Tenant propietario, por pantalla. La
/// autoverificación los exige todos. Si Inicio o Visión de cartera no dan la cifra
/// de Empresas es una divergencia declarada: además de exigirse cada cifra, se
/// escribe <see cref="Divergencia"/> como advertencia.
/// </summary>
/// <param name="TrabajadoresBloqueados">Trabajadores distintos bloqueados en al menos un Centro, como los cuenta Inicio.</param>
public sealed record EsperadoPilotoOutbound(
    int FilasMiTrabajo, int CumplimientoInicio, int CumplimientoVisionCartera, int CumplimientoEmpresa,
    IReadOnlyList<EsperadoCentroPilotoOutbound> Centros, int ParesExigidos, int ParesFaltantes, int Documentos,
    bool TodoAlDia, int TrabajadoresBloqueados = 0, string? Divergencia = null)
{
    /// <summary>Inicio o Visión de cartera no dan la cifra de Empresas: hay una divergencia que declarar.</summary>
    public bool InicioOVisionDeCarteraDivergenDeEmpresa =>
        CumplimientoInicio != CumplimientoEmpresa || CumplimientoVisionCartera != CumplimientoEmpresa;
}

/// <summary>
/// El catálogo declarativo de la siembra del piloto del Servicio TALVEG Outbound:
/// un Operador CAE externo de demostración y seis Tenants propietarios (T1..T6).
/// Todos los nombres propios viven aquí como constantes; el sembrador, la
/// autoverificación y los tests los referencian por constante, nunca por literal.
/// Las razones sociales son inventadas y se buscaron una a una sin encontrar
/// coincidencia con una empresa real; no es una consulta al Registro Mercantil.
///
/// <para>
/// <b>Para ampliar</b>: un Tenant nuevo nace como <see cref="EscenarioPilotoOutbound.Esqueleto"/>
/// y deja de serlo dándole Centros aquí, un constructor en el sembrador y una rama en
/// <see cref="Esperado"/> (o en <see cref="EsperadoGrande"/>, si lo suyo son intervalos).
/// </para>
/// </summary>
public static class CatalogoPilotoOutbound
{
    public const string NombreTenantOperador = "Coordinación Preventiva Nelvaris, S.L.";

    public const string NombreTenantT1 = "Instalaciones Gavrena, S.A.";
    public const string NombreTenantT2 = "Climatización Kedrobal, S.L.";
    public const string NombreTenantT3 = "Mantenimiento Industrial Ratvenco, S.L.";
    public const string NombreTenantT4 = "Montajes Tilvar, S.L.";
    public const string NombreTenantT5 = "Inspección Técnica Ostrimel, S.L.";
    public const string NombreTenantT6 = "Servicios Técnicos Anfelor, S.L.";

    public const string NombreGestoraPrimera = "Laura Benítez Soto";
    public const string NombreGestorSegundo = "Daniel Rubio Marín";
    public const string NombreCoordinadora = "Carmen Lozano Prieto";
    public const string NombreAdministradorOperador = "Jorge Sancho Vera";
    public const string NombreAdministradorT1 = "Beatriz Molina Ferrer";
    public const string NombreUsuarioDeClienteEmpresarialT1 = "Ramón Ibáñez Cuesta";

    public const string AptitudMedica = "Certificado de aptitud médica";
    public const string EntregaEpi = "Entrega de EPI";
    public const string FormacionArt19 = "Formación Art. 19";
    public const string InformacionArt18 = "Información Art. 18";
    public const string DocumentoIdentidad = "Documento de identidad";

    /// <summary>Un tipo de ámbito Trabajador que NO se exige por defecto: solo lo pide el Centro que lo incluye.</summary>
    public const string CarretillasElevadoras = "Carretillas elevadoras";

    /// <summary>
    /// Los únicos Tipos de documento que la siembra deja «no caduca». Son los de
    /// vencimiento manual cuya nota del catálogo (<see cref="TipoDocumentoSeedData"/>)
    /// dice que no tienen caducidad, o no les da ninguna: «Formación base, no consta
    /// caducidad», «No consta periodicidad de renovación», «Configurable si el convenio
    /// interno define vigencia», «Vigente mientras dure la relación laboral — sin fecha
    /// de caducidad propia» y «Vigente mientras continúe contratado — sin fecha de
    /// caducidad propia». Todos los demás Tipos sin vencimiento automático llevan una
    /// fecha anotada a mano, como dicen sus notas («fecha de vencimiento manual»), y la
    /// siembra se la pone (<see cref="FechasPilotoOutbound.EnRegla"/>).
    /// </summary>
    public static IReadOnlyList<string> TiposQueNoCaducan { get; } =
        ["Formación 60h (base convenio)", InformacionArt18, CarretillasElevadoras, "Contrato de Trabajo", "Alta en Seguridad Social"];

    /// <summary>Los cinco tipos de ámbito Trabajador exigidos por defecto. La siembra comprueba que el catálogo del Tenant dice lo mismo antes de escribir.</summary>
    public static IReadOnlyList<string> TiposExigidosDeTrabajador { get; } =
        [AptitudMedica, EntregaEpi, FormacionArt19, InformacionArt18, DocumentoIdentidad];

    /// <summary>El documento de Empresa de T2 que lleva el PDF pesado.</summary>
    public const string TipoDeEmpresaConPdfPesado = "Evaluación de Riesgos Laborales";

    /// <summary>En T2, el Centro con canal de plataforma (todas sus acreditaciones Aceptadas) y con un requisito que bloquea el acceso, cumplido.</summary>
    public const int IndiceCentroConPlataformaT2 = 0;

    public const string TipoBloqueanteCumplidoT2 = AptitudMedica;

    /// <summary>
    /// Lo que Inicio y Visión de cartera pintan para T4, que no tiene ningún documento
    /// (decisión del propietario: T4 queda siempre así; no se siembran documentos
    /// vencidos para forzar un 0 %).
    ///
    /// <para>
    /// Sin ningún Documento de Trabajador, Inicio y Visión de cartera miran los pares que
    /// exigen los Centros (<c>KpisDashboardDto.SinDocumentos</c>; decisión del propietario,
    /// 2026-10-09: «que no dé 100 %»): T4 exige diecinueve y no cumple ninguno, así que dan
    /// 0 %, lo mismo que Centros y Empresas. La autoverificación exige que lo medido en esas
    /// dos pantallas sea exactamente este número.
    /// </para>
    /// </summary>
    public const int CumplimientoDeInicioYVisionDeCarteraEnT4 = 0;

    /// <summary>
    /// T4: qué exige cada Centro (entre dos y tres tipos, ninguno bloqueante) y
    /// cuántos Trabajadores tiene. 3×3 + 3×2 + 2×2 = 19 pares.
    /// </summary>
    public static IReadOnlyList<(int Trabajadores, IReadOnlyList<string> Tipos)> RequisitosPorCentroT4 { get; } =
    [
        (3, [AptitudMedica, EntregaEpi, FormacionArt19]),
        (3, [AptitudMedica, EntregaEpi]),
        (2, [AptitudMedica, FormacionArt19])
    ];

    /// <summary>T2: Asignaciones activas (Trabajador, Centro) y las dos dadas de baja. Trece activas × cinco tipos = 65 pares.</summary>
    public static IReadOnlyList<(int Trabajador, int Centro)> AsignacionesActivasT2 { get; } =
    [
        (0, 0), (1, 0), (2, 1), (3, 1), (4, 2), (5, 2), (6, 3), (7, 3), (8, 4), (9, 4),
        (0, 1), (4, 3), (8, 0)
    ];

    public static IReadOnlyList<(int Trabajador, int Centro)> AsignacionesDeBajaT2 { get; } = [(10, 0), (11, 2)];

    /// <summary>
    /// T1, «grande»: 150 Trabajadores propios (los de las tres subcontratas son otros
    /// 25, ver <see cref="DisenoT1PilotoOutbound"/>), doce Clientes empresariales y
    /// veintiocho Centros de Trabajo: tres en cada uno de los cuatro primeros y dos en
    /// cada uno de los ocho siguientes.
    /// </summary>
    public static TenantPilotoOutbound T1 { get; } = new("T1", NombreTenantT1, EscenarioPilotoOutbound.Grande, false, DisenoT1PilotoOutbound.TrabajadoresPropios,
    [
        new("Logística Gorsenta, S.A.",
        [
            new("Plataforma logística de Illescas", "Illescas"), new("Almacén regulador de Azuqueca", "Azuqueca de Henares"),
            new("Centro de distribución de Getafe", "Getafe")
        ]),
        new("Conservas Palvecor, S.A.",
        [
            new("Fábrica de conservas de Calahorra", "Calahorra"), new("Planta de envasado de Alfaro", "Alfaro"),
            new("Almacén de expediciones de Tudela", "Tudela")
        ]),
        new("Distribuciones Kaltuva, S.A.",
        [
            new("Centro de distribución de Zaragoza", "Zaragoza"), new("Plataforma de cruce de Valladolid", "Valladolid"),
            new("Almacén de Vitoria", "Vitoria")
        ]),
        new("Química Hastrel, S.A.",
        [
            new("Planta química de Tarragona", "Tarragona"), new("Parque de tanques de Tarragona", "Tarragona"),
            new("Laboratorio de control de Reus", "Reus")
        ]),
        new("Energías Lindavo, S.A.", [new("Subestación de Albacete", "Albacete"), new("Parque fotovoltaico de Almansa", "Almansa")]),
        new("Componentes de Automoción Fervel, S.A.",
            [new("Planta de estampación de Martorell", "Martorell"), new("Planta de montaje de Almussafes", "Almussafes")]),
        new("Laboratorios Visquena, S.A.",
            [new("Planta farmacéutica de Alcalá de Henares", "Alcalá de Henares"), new("Almacén de materias primas de Torrejón", "Torrejón de Ardoz")]),
        new("Papelera Mirtaldo, S.A.", [new("Fábrica de papel de Hernani", "Hernani"), new("Almacén de bobinas de Andoain", "Andoain")]),
        new("Siderúrgica Moltrevi, S.A.", [new("Acería de Avilés", "Avilés"), new("Tren de laminación de Gijón", "Gijón")]),
        new("Terminal Portuaria Tusmedo, S.A.",
            [new("Terminal de contenedores de Algeciras", "Algeciras"), new("Terminal de graneles de Huelva", "Huelva")]),
        new("Centros de Datos Cinvelto, S.L.",
            [new("Centro de datos de Alcobendas", "Alcobendas"), new("Centro de datos de San Sebastián de los Reyes", "San Sebastián de los Reyes")]),
        new("Centros Comerciales Tramuel, S.A.", [new("Centro comercial de Málaga", "Málaga"), new("Centro comercial de Murcia", "Murcia")])
    ],
    ["Electricidad Uvalcor, S.L.", "Andamios Grebusal, S.L.", "Soldaduras Pivento, S.L."]);

    public static TenantPilotoOutbound T2 { get; } = new("T2", NombreTenantT2, EscenarioPilotoOutbound.TodoAlDia, true, 12,
    [
        new("Clínica Aldrevia, S.L.", [new("Clínica de Zaragoza", "Zaragoza"), new("Centro de especialidades de Zaragoza", "Zaragoza")]),
        new("Hoteles Olbran, S.L.", [new("Hotel de Getafe", "Getafe"), new("Hotel de Coslada", "Coslada")]),
        new("Centro Universitario Isbentia, S.L.", [new("Campus de Tarragona", "Tarragona")])
    ]);

    public static TenantPilotoOutbound T3 { get; } = new("T3", NombreTenantT3, EscenarioPilotoOutbound.Mitad, true, 10,
    [
        new("Aguas Fensira, S.A.", [new("Estación depuradora de Burgos", "Burgos"), new("Estación de bombeo de Burgos", "Burgos")]),
        new("Reciclajes Nuvaldo, S.L.", [new("Planta de clasificación de Tolosa", "Tolosa"), new("Taller de mantenimiento de Tolosa", "Tolosa")])
    ]);

    public static TenantPilotoOutbound T4 { get; } = new("T4", NombreTenantT4, EscenarioPilotoOutbound.TodoPendiente, false, 8,
    [
        new("Textiles Camorel, S.L.", [new("Tejeduría de Alcoy", "Alcoy"), new("Almacén de hilatura de Alcoy", "Alcoy")]),
        new("Cerámicas Quirvasa, S.A.", [new("Planta de azulejos de Villarreal", "Villarreal")])
    ]);

    public static TenantPilotoOutbound T5 { get; } = new("T5", NombreTenantT5, EscenarioPilotoOutbound.PocosTrabajadoresMuchosCentros, false, 4,
    [
        new("Plásticos y Envases Dunvareo, S.L.",
        [
            new("Planta de inyección de Valencia", "Valencia"), new("Planta de extrusión de Paterna", "Paterna"),
            new("Almacén de producto acabado de Ribarroja", "Ribarroja del Turia")
        ]),
        new("Cárnicas Lavirdo, S.L.",
        [
            new("Matadero de Guijuelo", "Guijuelo"), new("Sala de despiece de Guijuelo", "Guijuelo"), new("Secadero de Béjar", "Béjar")
        ]),
        new("Lácteos Xelmara, S.A.", [new("Central lechera de Lugo", "Lugo"), new("Quesería de Vilalba", "Vilalba")]),
        new("Bebidas Irmaleo, S.A.", [new("Planta embotelladora de Sevilla", "Sevilla"), new("Almacén regulador de Dos Hermanas", "Dos Hermanas")]),
        new("Vidrios Trelmona, S.L.", [new("Horno de fusión de Castellón", "Castellón de la Plana"), new("Planta de templado de Onda", "Onda")]),
        new("Fundiciones Bresolt, S.L.", [new("Fundición de Durango", "Durango"), new("Taller de mecanizado de Elorrio", "Elorrio")])
    ]);

    public static TenantPilotoOutbound T6 { get; } = new("T6", NombreTenantT6, EscenarioPilotoOutbound.Territorial, true, 24,
    [
        new("Almacenes Frigoríficos Orsemba, S.L.",
        [
            new("Barcelona · Almacén frigorífico de El Prat", "El Prat de Llobregat", DisenoT6PilotoOutbound.Barcelona),
            new("Madrid · Plataforma de frío de Coslada", "Coslada", DisenoT6PilotoOutbound.Madrid)
        ]),
        new("Residencias Ilvanda, S.L.",
        [
            new("Barcelona · Residencia de Sabadell", "Sabadell", DisenoT6PilotoOutbound.Barcelona),
            new("Santander · Residencia de El Sardinero", "Santander", DisenoT6PilotoOutbound.Santander)
        ]),
        new("Áridos Peltuan, S.L.", [new("Madrid · Planta de áridos de Arganda", "Arganda del Rey", DisenoT6PilotoOutbound.Madrid)]),
        new("Cartón Ondulado Narquil, S.A.", [new("Santander · Fábrica de cartón de Torrelavega", "Torrelavega", DisenoT6PilotoOutbound.Santander)]),
        new("Pinturas Tevidal, S.L.", [new("Barcelona · Fábrica de pinturas de Rubí", "Rubí", DisenoT6PilotoOutbound.Barcelona)]),
        new("Gases Industriales Umbritel, S.A.",
        [
            new("Madrid · Planta de envasado de Getafe", "Getafe", DisenoT6PilotoOutbound.Madrid),
            new("Santander · Depósito de Maliaño", "Maliaño", DisenoT6PilotoOutbound.Santander)
        ]),
        new("Mobiliario Ejuvan, S.L.", [new("Madrid · Nave de montaje de Alcobendas", "Alcobendas", DisenoT6PilotoOutbound.Madrid)]),
        new("Electrodomésticos Kimoral, S.A.", [new("Santander · Centro de distribución de Camargo", "Camargo", DisenoT6PilotoOutbound.Santander)]),
        new("Cosmética Cavelon, S.L.", [new("Barcelona · Laboratorio de Granollers", "Granollers", DisenoT6PilotoOutbound.Barcelona)])
    ],
    ["Limpiezas Industriales Ruspena, S.L.", "Transportes Bastrul, S.L."]);

    /// <summary>T1..T6, en el orden de la matriz.</summary>
    public static IReadOnlyList<TenantPilotoOutbound> Tenants { get; } = [T1, T2, T3, T4, T5, T6];

    /// <summary>
    /// El orden en que se siembran: los pequeños primero y T1 el último, para que una
    /// ejecución en un servidor con poca memoria haya dejado hecho lo más posible si
    /// se interrumpe en el grande.
    /// </summary>
    public static IReadOnlyList<TenantPilotoOutbound> EnOrdenDeSiembra { get; } = [T4, T3, T2, T5, T6, T1];

    /// <summary>Los siete Tenants, con el del Operador CAE externo al final: es el orden de retirada.</summary>
    public static IReadOnlyList<string> NombresTenants { get; } = [.. Tenants.Select(t => t.Nombre), NombreTenantOperador];

    /// <summary>Cuántos Trabajadores siembra el Tenant: los de su Empresa propia y, en T1, a continuación, los de sus subcontratas.</summary>
    public static int TrabajadoresSembrados(TenantPilotoOutbound tenant) =>
        tenant.Escenario == EscenarioPilotoOutbound.Grande ? DisenoT1PilotoOutbound.Trabajadores : tenant.Trabajadores;

    /// <summary>
    /// Los valores de la matriz que la autoverificación exige de cada Tenant
    /// propietario; <c>null</c> para un esqueleto (sin datos que medir) y para el
    /// Tenant grande, cuyo contrato es <see cref="EsperadoGrande"/>.
    /// Escritos a mano a partir del diseño, no derivados del sembrador: si el
    /// sembrador cambia y estos números no, la autoverificación falla.
    /// </summary>
    public static EsperadoPilotoOutbound? Esperado(TenantPilotoOutbound tenant) =>
        tenant.Escenario switch
        {
            // «Todo al día» es 100 % en las cuatro pantallas y ninguna fila en Mi trabajo, que solo trae documentos de
            // Trabajador. Sus Centros no están en verde: los dos certificados mensuales de la Empresa propia, que un
            // documento coherente con su tipo solo puede tener «Próximo», tiñen de ámbar todos los Centros de la Empresa.
            EscenarioPilotoOutbound.TodoAlDia => new(
                FilasMiTrabajo: 0, CumplimientoInicio: 100, CumplimientoVisionCartera: 100, CumplimientoEmpresa: 100,
                Centros: [.. tenant.Centros.Select(c => new EsperadoCentroPilotoOutbound(c.Nombre, EstadoCentro.Proximo, 100))],
                ParesExigidos: 65, ParesFaltantes: 0, Documentos: 77, TodoAlDia: true),

            // Diez Trabajadores, cada uno en un Centro que le exige dos tipos: 20 pares y 20 documentos, la mitad
            // Vencidos. Mi trabajo: una fila por documento vencido; la Visita, a quince días, no añade ninguna.
            EscenarioPilotoOutbound.Mitad => new(
                FilasMiTrabajo: 10, CumplimientoInicio: 50, CumplimientoVisionCartera: 50, CumplimientoEmpresa: 50,
                Centros:
                [
                    new(tenant.Centros[0].Nombre, EstadoCentro.Vencido, 50), new(tenant.Centros[1].Nombre, EstadoCentro.Vencido, 50),
                    new(tenant.Centros[2].Nombre, EstadoCentro.Vencido, 50), new(tenant.Centros[3].Nombre, EstadoCentro.Vencido, 50)
                ],
                // Documentos: 20 de Trabajador (10 × 2) y los 13 que se exigen por defecto a la Empresa propia, Vigentes.
                ParesExigidos: 20, ParesFaltantes: 0, Documentos: 33, TodoAlDia: false),

            // Diecinueve pares exigidos, ninguno con documento: diecinueve filas «Falta».
            EscenarioPilotoOutbound.TodoPendiente => new(
                FilasMiTrabajo: 19,
                CumplimientoInicio: CumplimientoDeInicioYVisionDeCarteraEnT4,
                CumplimientoVisionCartera: CumplimientoDeInicioYVisionDeCarteraEnT4,
                CumplimientoEmpresa: 0,
                Centros: [.. tenant.Centros.Select(c => new EsperadoCentroPilotoOutbound(c.Nombre, EstadoCentro.Faltante, 0))],
                ParesExigidos: 19, ParesFaltantes: 19, Documentos: 0, TodoAlDia: false),

            // Mi trabajo: dos Trabajadores bloqueados en el Centro de la periodicidad especial (2), la Formación
            // vencida (1) y el EPI urgente (1). Documentos: 4 × 5, uno Vencido: 19 de 20 = 95 %. Pares: 44
            // Asignaciones × 5 = 220; la Formación vencida falla en los ocho Centros de su Trabajador: 212 de
            // 220 = 96 %. Los ocho primeros Centros tienen a los cuatro Trabajadores (19 de 20 pares, y el
            // vencido los pone en rojo); los dos siguientes, a tres (15 de 15, con el EPI urgente); los cuatro
            // últimos, a dos o a uno, sin nada pendiente de sus Trabajadores: los deja en «Próximo» lo único que
            // les llega, los dos certificados mensuales de la Empresa propia.
            EscenarioPilotoOutbound.PocosTrabajadoresMuchosCentros => new(
                FilasMiTrabajo: 4, CumplimientoInicio: 95, CumplimientoVisionCartera: 95, CumplimientoEmpresa: 96,
                Centros:
                [
                    new(tenant.Centros[0].Nombre, EstadoCentro.Vencido, 95), new(tenant.Centros[1].Nombre, EstadoCentro.Vencido, 95),
                    new(tenant.Centros[2].Nombre, EstadoCentro.Vencido, 95), new(tenant.Centros[3].Nombre, EstadoCentro.Vencido, 95),
                    new(tenant.Centros[4].Nombre, EstadoCentro.Vencido, 95), new(tenant.Centros[5].Nombre, EstadoCentro.Vencido, 95),
                    new(tenant.Centros[6].Nombre, EstadoCentro.Vencido, 95), new(tenant.Centros[7].Nombre, EstadoCentro.Vencido, 95),
                    new(tenant.Centros[8].Nombre, EstadoCentro.Urgente, 100), new(tenant.Centros[9].Nombre, EstadoCentro.Urgente, 100),
                    new(tenant.Centros[10].Nombre, EstadoCentro.Proximo, 100), new(tenant.Centros[11].Nombre, EstadoCentro.Proximo, 100),
                    new(tenant.Centros[12].Nombre, EstadoCentro.Proximo, 100), new(tenant.Centros[13].Nombre, EstadoCentro.Proximo, 100)
                ],
                // Documentos: 20 de Trabajador (4 × 5) y los 13 que se exigen por defecto a la Empresa propia, en regla.
                ParesExigidos: 220, ParesFaltantes: 0, Documentos: 33, TodoAlDia: false, TrabajadoresBloqueados: 2,
                Divergencia:
                    "Inicio y Visión de cartera cuentan documentos (19 de 20 al día) y Centros y Empresas cuentan pares " +
                    "exigidos (212 de 220): el mismo documento vencido cuenta una vez allí y ocho aquí, una por cada Centro " +
                    "que lo exige a su Trabajador."),

            // Mi trabajo: 5 vencidos + 7 «Falta» (uno por Centro que lo exige: el Trabajador 4 está en dos, y el
            // desplazado no tiene el tipo propio del Centro de destino) + 1 requisito bloqueante pendiente + 4
            // urgentes + 2 próximos = 19 (Barcelona 11, Madrid 6, Santander 2). La Visita del desplazamiento no
            // añade filas: Mi trabajo solo trae Visitas urgentes. Documentos de Trabajador: 24 × 5 − 5 ausentes +
            // 2 del tipo propio = 117, cinco Vencidos: 112 de 117 = 96 %; con los seis de las dos subcontratas, 123.
            // Pares: 30 Asignaciones × 5 + el Centro de destino, que pasa de 2 × 5 a 3 × 6 = 158; fallan 13 (los
            // Trabajadores 0 y 4 están en dos Centros): 145 de 158 = 92 %. El Centro de destino: 15 de 18 = 83 %.
            EscenarioPilotoOutbound.Territorial => new(
                FilasMiTrabajo: 19, CumplimientoInicio: 96, CumplimientoVisionCartera: 96, CumplimientoEmpresa: 92,
                Centros:
                [
                    new(tenant.CentrosDe(DisenoT6PilotoOutbound.Barcelona)[0].Nombre, EstadoCentro.Faltante, 83),
                    new(tenant.CentrosDe(DisenoT6PilotoOutbound.Barcelona)[1].Nombre, EstadoCentro.Vencido, 80),
                    new(tenant.CentrosDe(DisenoT6PilotoOutbound.Barcelona)[2].Nombre, EstadoCentro.Faltante, 80),
                    new(tenant.CentrosDe(DisenoT6PilotoOutbound.Barcelona)[3].Nombre, EstadoCentro.Faltante, 87),
                    new(tenant.CentrosDe(DisenoT6PilotoOutbound.Madrid)[0].Nombre, EstadoCentro.Vencido, 90),
                    new(tenant.CentrosDe(DisenoT6PilotoOutbound.Madrid)[1].Nombre, EstadoCentro.Urgente, 100),
                    new(tenant.CentrosDe(DisenoT6PilotoOutbound.Madrid)[2].Nombre, EstadoCentro.Urgente, 100),
                    new(tenant.CentrosDe(DisenoT6PilotoOutbound.Madrid)[3].Nombre, EstadoCentro.Faltante, 87),
                    // Los dos de Santander sin nada pendiente de sus Trabajadores quedan en «Próximo» por los dos
                    // certificados mensuales de la Empresa propia, igual que el que ya lo estaba por un documento de Trabajador.
                    new(tenant.CentrosDe(DisenoT6PilotoOutbound.Santander)[0].Nombre, EstadoCentro.Proximo, 100),
                    new(tenant.CentrosDe(DisenoT6PilotoOutbound.Santander)[1].Nombre, EstadoCentro.Proximo, 100),
                    new(tenant.CentrosDe(DisenoT6PilotoOutbound.Santander)[2].Nombre, EstadoCentro.Urgente, 100),
                    new(tenant.CentrosDe(DisenoT6PilotoOutbound.Santander)[3].Nombre, EstadoCentro.Proximo, 100)
                ],
                // Documentos: los 123 de Trabajadores y subcontratas, y los 13 que se exigen por defecto a la Empresa propia, en regla.
                ParesExigidos: 158, ParesFaltantes: 7, Documentos: 136, TodoAlDia: false, TrabajadoresBloqueados: 1,
                Divergencia:
                    "Inicio y Visión de cartera cuentan documentos de Trabajador (112 de 117 al día) y Centros y Empresas " +
                    "cuentan pares exigidos (145 de 158): un documento que falta no existe para Inicio y sí es un par " +
                    "incumplido en cada Centro que lo exige."),

            _ => null
        };

    /// <summary>
    /// Los valores que la autoverificación exige del Tenant grande; <c>null</c> para
    /// los demás. Escritos a mano a partir de <see cref="DisenoT1PilotoOutbound"/>:
    ///
    /// <para>
    /// Estructura, exacta: 150 Trabajadores propios y 25 de tres subcontratas, doce
    /// Clientes empresariales y veintiocho Centros. Documentos: 175 × 5 − 21 ausentes
    /// = 854 de Trabajador, 13 + 1 de la Empresa propia, 2 × 4 de los Vehículos y
    /// 3 × 3 de las subcontratas: 885, cada uno con su PDF. Pares exigidos: 193
    /// Asignaciones activas × 5 = 965, de los que 21 no tienen documento.
    /// </para>
    ///
    /// <para>
    /// Por intervalo. Empresas: 708 de los 840 pares de la Empresa propia, el 84 %; se
    /// admite 82..88, el margen de la matriz («≈85 %»). Inicio y Visión de cartera
    /// cuentan documentos de Trabajador, no pares: 854 menos 37 Vencidos y 61 sin
    /// confirmar, 756 de 854, el 89 %; se admite 87..91. Mi trabajo: 37 vencidos, 21
    /// «Falta», 3 requisitos bloqueantes pendientes, 15 urgentes y 12 próximos, más
    /// las acreditaciones externas (del orden de 28 Pendientes de subir, 26 Subidas,
    /// una Rechazada y una vencida en plataforma): unas 144 filas, 145 la víspera y
    /// el día de la demostración por la Visita. T1 es la excepción declarada al
    /// volumen de 40 a 80 filas de la cartera; se admite 100..250.
    /// </para>
    /// </summary>
    public static EsperadoGrandePilotoOutbound? EsperadoGrande(TenantPilotoOutbound tenant) =>
        tenant.Escenario != EscenarioPilotoOutbound.Grande
            ? null
            : new(
                TrabajadoresPropios: 150, TrabajadoresDeSubcontrata: 25, Subcontratas: 3, ClientesEmpresariales: 12, Centros: 28,
                Documentos: 885, ParesExigidos: 965, ParesFaltantes: 21,
                CumplimientoEmpresa: new(82, 88), CumplimientoInicioYVisionDeCartera: new(87, 91), FilasMiTrabajo: new(100, 250),
                CasosDeEstado: CasosDeEstadoPilotoOutbound.Todos,
                DiasHastaLaVisita: 1, TrabajadoresDeLaVisita: 2, ExigidosDeTrabajadorEnElPaquete: 10, ExigidosDeEmpresaEnElPaquete: 13);
}

/// <summary>
/// Con qué nombres, identificadores y direcciones rotula la siembra a las personas,
/// las Empresas y los Centros de Trabajo del piloto. Solo es texto: ningún estado,
/// porcentaje ni fila de la matriz depende de lo que hay aquí. Todo es inventado y
/// sale del catálogo y de un índice, sin azar ni reloj, de modo que dos siembras
/// escriben lo mismo.
///
/// <para>
/// <b>Personas.</b> El primer apellido y el segundo se eligen por separado, de dos
/// listas de tamaños primos entre sí (31 y 29): un ordinal menor que 31 × 29 = 899
/// tiene una pareja de apellidos que no tiene ningún otro. Los Trabajadores del
/// lote ocupan los ordinales desde cero, seguidos y en el orden de la matriz
/// (<see cref="Trabajador"/>); los contactos de agenda, los que quedan a partir de
/// <see cref="OrdinalesDeTrabajador"/> (<see cref="Contacto"/>). Así ningún
/// Trabajador comparte los dos apellidos con otro Trabajador ni con un contacto,
/// en ninguno de los seis Tenants.
/// </para>
/// </summary>
public static class IdentidadesPilotoOutbound
{
    public static readonly string[] NombresDePila =
    [
        "Lucía", "Andrés", "Noelia", "Rubén", "Paula", "Héctor", "Irene", "Óscar", "Carla", "Mateo", "Sonia", "Adrián",
        "Elena", "Javier", "Nuria", "Sergio", "Marta", "Iván", "Rocío", "Tomás", "Silvia", "Gonzalo", "Alicia", "Víctor"
    ];

    public static readonly string[] PrimerosApellidos =
    [
        "Navarro", "Ferrer", "Molina", "Soler", "Cano", "Ibarra", "Lozano", "Vega", "Campos", "Pastor", "Salas", "Bravo",
        "Herrero", "Gallego", "Santos", "Crespo", "Aguilar", "Blanco", "Calvo", "Delgado", "Esteban", "Fuentes", "Guerrero",
        "Hidalgo", "Iglesias", "Montero", "Pascual", "Redondo", "Sáez", "Trujillo", "Vicente"
    ];

    public static readonly string[] SegundosApellidos =
    [
        "Gil", "Pons", "Ríos", "Vidal", "Sanz", "Marín", "Ortiz", "Lara", "Rey", "Duque", "Nieto", "Luna", "Mora", "Peña",
        "Arias", "Bermejo", "Cordero", "Durán", "Espinosa", "Franco", "Gallardo", "Herranz", "Izquierdo", "Lorenzo", "Medina",
        "Olmos", "Paredes", "Roldán", "Tejero"
    ];

    /// <summary>Los ordinales reservados a los Trabajadores del lote. Hoy son 233; con más de estos, la siembra falla y lo dice.</summary>
    public const int OrdinalesDeTrabajador = 400;

    /// <summary>El ordinal, en <see cref="Cif"/>, de la primera subcontrata de un Tenant: por debajo van su Empresa propia (0) y sus Clientes empresariales (1, 2…).</summary>
    public const int OrdinalDeLaPrimeraSubcontrata = 20;

    private static int Parejas => PrimerosApellidos.Length * SegundosApellidos.Length;

    /// <summary>
    /// El nombre de pila y los dos apellidos del Trabajador <paramref name="i"/> del
    /// Tenant (en T1, los de sus subcontratas siguen a los propios). El nombre
    /// completo no se repite en todo el lote.
    /// </summary>
    public static (string Nombre, string Apellidos) Trabajador(TenantPilotoOutbound tenant, int i) => Persona(OrdinalEnElLote(tenant, i));

    /// <summary>
    /// El nombre completo de un contacto de agenda. Dentro de un Tenant, dos
    /// ordinales distintos dan dos personas distintas; entre Tenants puede repetirse
    /// la pareja de apellidos, nunca con la de un Trabajador.
    /// </summary>
    public static string Contacto(TenantPilotoOutbound tenant, int ordinal)
    {
        var (nombre, apellidos) = Persona(OrdinalesDeTrabajador + (IndiceDe(tenant) * 83 + ordinal) % (Parejas - OrdinalesDeTrabajador));
        return $"{nombre} {apellidos}";
    }

    // Los multiplicadores no son múltiplos del tamaño de su lista: recorren cada una entera antes de repetir, y dos
    // ordinales seguidos no dan apellidos vecinos.
    private static (string Nombre, string Apellidos) Persona(int ordinal) => (
        NombresDePila[ordinal * 5 % NombresDePila.Length],
        $"{PrimerosApellidos[ordinal * 7 % PrimerosApellidos.Length]} {SegundosApellidos[(ordinal * 5 + 3) % SegundosApellidos.Length]}");

    /// <summary>
    /// El documento de identidad del Trabajador <paramref name="i"/> del Tenant, con
    /// su letra de control. El multiplicador es impar y no acaba en cinco, así que no
    /// comparte factor con los ochenta millones del módulo: dos Trabajadores del lote
    /// no reciben el mismo número, y los de dos Trabajadores seguidos distan millones.
    /// </summary>
    public static string Dni(TenantPilotoOutbound tenant, int i) =>
        DatosPruebaSeeder.GenerarDniValido(10_000_000 + (int)((OrdinalEnElLote(tenant, i) + 1L) * 37_139_213 % 80_000_000));

    private static int OrdinalEnElLote(TenantPilotoOutbound tenant, int i)
    {
        var ordinal = CatalogoPilotoOutbound.Tenants.Take(IndiceDe(tenant)).Sum(CatalogoPilotoOutbound.TrabajadoresSembrados) + i;
        if (i < 0 || i >= CatalogoPilotoOutbound.TrabajadoresSembrados(tenant) || ordinal >= OrdinalesDeTrabajador)
            throw new InvalidOperationException(
                $"{tenant.Clave}: el Trabajador {i} queda fuera de los {OrdinalesDeTrabajador} ordinales que la siembra del piloto reserva a los " +
                "Trabajadores del lote, o fuera de los que su Tenant declara: su nombre y su documento de identidad podrían repetirse.");

        return ordinal;
    }

    private static int IndiceDe(TenantPilotoOutbound tenant) => CatalogoPilotoOutbound.Tenants.ToList().IndexOf(tenant);

    // Códigos de provincia con que empieza un identificador fiscal de sociedad: es solo verosimilitud, no la sede de nadie.
    private static readonly int[] Provincias = [28, 8, 46, 41, 48, 50, 39, 15, 29, 33, 3, 47, 45];

    /// <summary>
    /// El identificador fiscal de una Empresa del piloto: el ordinal 0 es la Empresa
    /// propia del Tenant; del 1 en adelante, sus Clientes empresariales, y desde
    /// <see cref="OrdinalDeLaPrimeraSubcontrata"/>, sus subcontratas. La letra es la
    /// de la forma jurídica que dice la razón social, y el dígito de control, el que
    /// le corresponde. Las cinco últimas cifras no se repiten en todo el lote: 7919
    /// no comparte factor con cien mil.
    /// </summary>
    public static string Cif(TenantPilotoOutbound tenant, int ordinal)
    {
        var razonSocial = ordinal == 0
            ? tenant.Nombre
            : ordinal < OrdinalDeLaPrimeraSubcontrata
                ? tenant.ClientesEmpresariales[ordinal - 1].RazonSocial
                : (tenant.Subcontratas ?? [])[ordinal - OrdinalDeLaPrimeraSubcontrata];

        var k = IndiceDe(tenant) * 100 + ordinal;
        var numero = Provincias[k * 3 % Provincias.Length] * 100_000 + (k * 7_919 + 4_621) % 100_000;
        return LetraDeLaFormaJuridica(razonSocial) + DatosPruebaSeeder.GenerarCifValido(numero)[1..];
    }

    /// <summary>La letra con que empieza el identificador fiscal de una sociedad anónima (A) o limitada (B), que son las dos formas del catálogo.</summary>
    public static char LetraDeLaFormaJuridica(string razonSocial) =>
        razonSocial.EndsWith(", S.A.", StringComparison.Ordinal) ? 'A'
        : razonSocial.EndsWith(", S.L.", StringComparison.Ordinal) ? 'B'
        : throw new InvalidOperationException(
            $"«{razonSocial}» no acaba en «, S.A.» ni en «, S.L.»: la siembra del piloto no sabe con qué letra empieza su identificador fiscal.");

    private static readonly string[] Vias =
    [
        "Avenida de la Industria", "Calle de la Estación", "Carretera de Circunvalación", "Calle del Comercio",
        "Camino de la Vega", "Avenida de la Constitución", "Calle Mayor", "Ronda del Polígono"
    ];

    /// <summary>La dirección postal del Centro número <paramref name="numero"/> (desde 1) de su Tenant.</summary>
    public static string Direccion(int numero, string localidad) => $"{Vias[(numero - 1) * 3 % Vias.Length]}, {numero * 7}, {localidad}";

    /// <summary>
    /// El puesto u oficio del Trabajador <paramref name="i"/>, de un repertorio corto
    /// acorde con la actividad que dice el nombre de su Empresa. Es el dato de la
    /// ficha; no decide ningún requisito documental.
    /// </summary>
    public static string Puesto(TenantPilotoOutbound tenant, int i)
    {
        if (tenant.Escenario == EscenarioPilotoOutbound.Grande && i >= DisenoT1PilotoOutbound.TrabajadoresPropios)
            return PuestosDeLasSubcontratasDeT1[DisenoT1PilotoOutbound.SubcontrataDe(i)];

        string[] puestos = tenant.Escenario switch
        {
            EscenarioPilotoOutbound.Grande =>
                ["Oficial electricista", "Instalador de baja tensión", "Montador de estructuras", "Técnico de puesta en marcha", "Encargado de obra", "Ayudante de instalaciones"],
            EscenarioPilotoOutbound.TodoAlDia => ["Técnico frigorista", "Instalador de climatización", "Oficial de mantenimiento", "Ayudante de climatización"],
            EscenarioPilotoOutbound.Mitad => ["Mecánico industrial", "Electromecánico", "Soldador", "Tubero", "Oficial de mantenimiento"],
            EscenarioPilotoOutbound.TodoPendiente => ["Montador", "Soldador", "Calderero", "Encargado de montaje"],
            EscenarioPilotoOutbound.PocosTrabajadoresMuchosCentros =>
                ["Inspector técnico", "Técnico de ensayos no destructivos", "Inspector de instalaciones", "Técnico de calidad"],
            _ => ["Técnico de mantenimiento", "Oficial electricista", "Fontanero", "Operario de servicios", "Encargado de zona"]
        };

        return puestos[i * 7 % puestos.Length];
    }

    // En el orden de las subcontratas de T1 en el catálogo: electricidad, andamios y soldadura.
    private static readonly string[] PuestosDeLasSubcontratasDeT1 = ["Oficial electricista", "Montador de andamios", "Soldador"];

    /// <summary>Las tres letras, en mayúsculas y sin acentos, con que empieza el código de un Centro sin zona: las de la primera palabra con significado de su localidad.</summary>
    public static string LetrasDeLaLocalidad(string localidad)
    {
        var palabra = Palabras(localidad).First();
        return palabra[..Math.Min(3, palabra.Length)].ToUpperInvariant();
    }

    /// <summary>La parte local de la dirección del responsable de prevención de una Empresa: «prevencion.gavrena».</summary>
    public static string EtiquetaDePrevencion(string razonSocial) => $"prevencion.{NombreCortoDe(razonSocial)}";

    public static string EtiquetaDeAdministracion(string razonSocial) => $"administracion.{NombreCortoDe(razonSocial)}";

    /// <summary>La del contacto interno de una zona de la Empresa propia: «coordinacion.barcelona.anfelor».</summary>
    public static string EtiquetaDeCoordinacionDeZona(ZonaPilotoOutbound zona, string razonSocial) =>
        $"coordinacion.{string.Join('-', Palabras(zona.Nombre))}.{NombreCortoDe(razonSocial)}";

    /// <summary>La del técnico de coordinación CAE de un Cliente empresarial: «cae.gorsenta».</summary>
    public static string EtiquetaDeCoordinacionCae(string razonSocial) => $"cae.{NombreCortoDe(razonSocial)}";

    /// <summary>La del contacto de un Centro de Trabajo: «coordinacion.plataforma-logistica-illescas».</summary>
    public static string EtiquetaDeCoordinacionDeAccesos(CentroPilotoOutbound centro) => $"coordinacion.{NombreCortoDe(centro)}";

    /// <summary>La del canal por correo de un Centro de Trabajo, distinta de la de su contacto: «accesos.parque-fotovoltaico-almansa».</summary>
    public static string EtiquetaDeSolicitudesDeAcceso(CentroPilotoOutbound centro) => $"accesos.{NombreCortoDe(centro)}";

    /// <summary>El usuario de la Empresa propia en el portal de un Cliente empresarial: «gestion.kedrobal».</summary>
    public static string UsuarioDePortal(string razonSocial) => $"gestion.{NombreCortoDe(razonSocial)}";

    /// <summary>La palabra que distingue a una Empresa del catálogo: la última antes de la forma jurídica.</summary>
    private static string NombreCortoDe(string razonSocial)
    {
        var coma = razonSocial.LastIndexOf(',');
        return Palabras(coma < 0 ? razonSocial : razonSocial[..coma]).Last();
    }

    /// <summary>El nombre del Centro sin su zona, en minúsculas y con guiones. Los nombres de Centro no se repiten dentro de un Tenant.</summary>
    private static string NombreCortoDe(CentroPilotoOutbound centro)
    {
        var prefijoDeZona = centro.Zona is { } zona ? $"{zona.Nombre} · " : null;
        var nombre = prefijoDeZona is not null && centro.Nombre.StartsWith(prefijoDeZona, StringComparison.Ordinal)
            ? centro.Nombre[prefijoDeZona.Length..]
            : centro.Nombre;
        return string.Join('-', Palabras(nombre));
    }

    private static readonly string[] PalabrasSinSignificado = ["de", "del", "el", "la", "los", "las", "y"];

    /// <summary>Las palabras con significado de un texto, en minúsculas, sin acentos y solo con letras y cifras.</summary>
    private static IEnumerable<string> Palabras(string texto)
    {
        var llano = string.Concat(texto.Normalize(NormalizationForm.FormD)
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            .Select(c => char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' '));

        return llano.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(p => !PalabrasSinSignificado.Contains(p));
    }
}
