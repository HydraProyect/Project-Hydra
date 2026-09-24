using CaeManager.Application.AsistenteIa.Decisiones;

namespace CaeManager.Application.AsistenteIa.Candidatos;

public enum SituacionTenantDestino
{
    /// <summary>Un solo Tenant posible: se propone sin preguntar y el plan lo enseña.</summary>
    Unico,

    /// <summary>La orden no sitúa el Tenant: se pregunta, ofreciendo solo los de la cartera.</summary>
    Elegir,

    /// <summary>El plan juntaría datos de dos Tenants o más. Bloqueante: no se puede confirmar.</summary>
    Mezcla,

    /// <summary>
    /// El Gestor CAE eligió a mano un Tenant y los datos de la orden son de otro.
    /// Bloqueante hasta que cambie al recomendado o se vuelvan a buscar los datos
    /// en el que eligió: un Id de un Tenant no existe en otro.
    /// </summary>
    Discrepancia,

    /// <summary>El Gestor CAE no tiene ningún Tenant en cartera: no hay dónde ejecutar.</summary>
    SinCartera,
}

/// <summary>
/// En qué Tenant beneficiario se ejecutaría la orden, y si hace falta preguntarlo.
/// <paramref name="Tenant"/> va informado en <see cref="SituacionTenantDestino.Unico"/>;
/// <paramref name="Tenants"/> son las opciones en <see cref="SituacionTenantDestino.Elegir"/>
/// y los Tenants que se mezclan en <see cref="SituacionTenantDestino.Mezcla"/>.
/// <paramref name="Recomendado"/> es el Tenant al que se recomienda cambiar en
/// <see cref="SituacionTenantDestino.Discrepancia"/>.
/// <paramref name="DistintoDePantalla"/> avisa de que el destino no es el Tenant
/// que el Gestor CAE tiene abierto en pantalla, para resaltarlo.
/// </summary>
public record TenantDestinoDto(
    SituacionTenantDestino Situacion,
    TenantDeCarteraDto? Tenant,
    IReadOnlyList<TenantDeCarteraDto> Tenants,
    string Motivo,
    TenantDeCarteraDto? Recomendado = null,
    bool DistintoDePantalla = false)
{
    public bool Bloquea => Situacion is SituacionTenantDestino.Mezcla
        or SituacionTenantDestino.Discrepancia
        or SituacionTenantDestino.SinCartera;
}

/// <summary>
/// Regla del Tenant destino, fijada por el propietario el 2026-09-24 para el
/// Gestor CAE con Asignación de Cartera sobre varios Tenants. Determinista: no
/// consulta al modelo ni lee el texto de la orden, solo el sello de los
/// candidatos elegidos (<see cref="CandidatoSelladoDto.TenantId"/>). Lo que el
/// Gestor CAE escribe o dice llega aquí como candidatos elegidos, también el
/// Tenant que nombre (campo <c>tenant</c>).
/// <list type="number">
/// <item>Los datos de la orden mandan: si todos salen de un mismo Tenant, ese es
/// el destino y se propone sin preguntar, aunque no sea el de la pantalla.</item>
/// <item>Si los datos salen de Tenants distintos, el plan no se puede confirmar:
/// nunca se da de alta a un Trabajador de un Tenant en otro.</item>
/// <item>Si el Gestor CAE cambia el Tenant a mano (chip del chat) y los datos son
/// de otro, se bloquea y se le recomienda el Tenant de los datos.</item>
/// <item>Si nada lo sitúa, el destino es el Tenant elegido a mano; si no lo hay,
/// el de la pantalla; si tampoco, el único de la cartera; con varios, se pregunta
/// ofreciendo solo los de la cartera.</item>
/// </list>
/// El Tenant elegido a mano tiene que ser de la cartera. El de la pantalla es
/// solo un valor por defecto: si no es de la cartera se ignora.
/// </summary>
public static class ResolucionTenantDestino
{
    public static TenantDestinoDto Resolver(
        CandidatosAsistenteDto candidatos,
        IReadOnlyList<SeleccionCandidatoDto> selecciones,
        Guid? tenantElegido = null,
        Guid? tenantPantalla = null)
    {
        if (candidatos.Tenants.Count == 0)
            return new(SituacionTenantDestino.SinCartera, null, [],
                "No tienes ningún Tenant en tu cartera: no hay dónde ejecutar la orden.");

        TenantDeCarteraDto? elegido = null;
        if (tenantElegido is { } idElegido)
            elegido = candidatos.Tenants.FirstOrDefault(t => t.TenantId == idElegido)
                ?? throw new ArgumentException("El Tenant elegido no es de la cartera.", nameof(tenantElegido));

        var pantalla = candidatos.Tenants.FirstOrDefault(t => t.TenantId == tenantPantalla);

        var implicados = new HashSet<Guid>();
        foreach (var seleccion in selecciones)
        {
            if (seleccion.CandidatoId is not { } candidatoId)
                continue;
            // Un Id que no está entre los candidatos no tiene sello: se rechaza
            // en vez de ignorarlo, porque ignorarlo dejaría el dato sin Tenant.
            var tenant = candidatos.TenantDe(seleccion.Campo, candidatoId)
                ?? throw new ArgumentException(
                    $"El candidato elegido para «{seleccion.Campo}» no está entre los enviados.", nameof(selecciones));
            implicados.Add(tenant);
        }

        var deLosDatos = candidatos.Tenants.Where(t => implicados.Contains(t.TenantId)).ToList();

        if (deLosDatos.Count > 1)
            return new(SituacionTenantDestino.Mezcla, null, deLosDatos,
                $"La orden junta datos de {string.Join(" y ", deLosDatos.Select(t => t.Nombre))}. " +
                "Una gestión se ejecuta en un solo Tenant: sepárala en una orden por Tenant.");

        if (deLosDatos.Count == 1)
        {
            var correcto = deLosDatos[0];
            if (elegido is not null && elegido.TenantId != correcto.TenantId)
                return new(SituacionTenantDestino.Discrepancia, null, [elegido, correcto],
                    $"Has elegido {elegido.Nombre}, pero los datos de la orden son de {correcto.Nombre}. " +
                    $"Te recomendamos cambiar a {correcto.Nombre}.",
                    Recomendado: correcto);
            return Unico(correcto, $"Todos los datos son de {correcto.Nombre}.", pantalla);
        }

        if (elegido is not null)
            return Unico(elegido, $"Has elegido {elegido.Nombre}.", pantalla);
        if (pantalla is not null)
            return Unico(pantalla, $"{pantalla.Nombre} es el Tenant que tienes abierto.", pantalla);
        if (candidatos.Tenants.Count == 1)
            return Unico(candidatos.Tenants[0], $"{candidatos.Tenants[0].Nombre} es el único Tenant de tu cartera.", pantalla);
        return new(SituacionTenantDestino.Elegir, null, candidatos.Tenants,
            "La orden no dice en qué Tenant se trabaja. Elige uno de tu cartera.");
    }

    private static TenantDestinoDto Unico(TenantDeCarteraDto destino, string motivo, TenantDeCarteraDto? pantalla) =>
        new(SituacionTenantDestino.Unico, destino, [destino], motivo,
            DistintoDePantalla: pantalla is not null && pantalla.TenantId != destino.TenantId);
}
