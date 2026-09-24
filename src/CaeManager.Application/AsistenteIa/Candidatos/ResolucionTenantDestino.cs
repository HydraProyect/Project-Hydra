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

    /// <summary>El Gestor CAE no tiene ningún Tenant en cartera: no hay dónde ejecutar.</summary>
    SinCartera,
}

/// <summary>
/// En qué Tenant beneficiario se ejecutaría la orden, y si hace falta preguntarlo.
/// <paramref name="Tenant"/> solo va informado en <see cref="SituacionTenantDestino.Unico"/>;
/// <paramref name="Tenants"/> son las opciones en <see cref="SituacionTenantDestino.Elegir"/>
/// y los Tenants que se mezclan en <see cref="SituacionTenantDestino.Mezcla"/>.
/// </summary>
public record TenantDestinoDto(
    SituacionTenantDestino Situacion,
    TenantDeCarteraDto? Tenant,
    IReadOnlyList<TenantDeCarteraDto> Tenants,
    string Motivo)
{
    public bool Bloquea => Situacion is SituacionTenantDestino.Mezcla or SituacionTenantDestino.SinCartera;
}

/// <summary>
/// Regla del Tenant destino, fijada por el propietario el 2026-09-24 para el
/// Gestor CAE con Asignación de Cartera sobre varios Tenants. Determinista: no
/// consulta al modelo ni lee el texto de la orden, solo el sello de los
/// candidatos elegidos (<see cref="CandidatoSelladoDto.TenantId"/>).
/// <list type="number">
/// <item>Si todos los datos elegidos salen de un mismo Tenant, ese es el destino
/// y se propone sin preguntar.</item>
/// <item>Si ninguno lo sitúa, con un solo Tenant en la cartera ese es el destino;
/// con varios, se pregunta ofreciendo solo los de la cartera.</item>
/// <item>Si los datos elegidos salen de Tenants distintos, el plan no se puede
/// confirmar: nunca se da de alta a un Trabajador de un Tenant en otro.</item>
/// <item>El Tenant que elija el Gestor CAE tiene que ser de la cartera, y
/// cuenta como un dato más: un candidato de otro Tenant es mezcla.</item>
/// </list>
/// </summary>
public static class ResolucionTenantDestino
{
    public static TenantDestinoDto Resolver(
        CandidatosAsistenteDto candidatos,
        IReadOnlyList<SeleccionCandidatoDto> selecciones,
        Guid? tenantElegido = null)
    {
        if (candidatos.Tenants.Count == 0)
            return new(SituacionTenantDestino.SinCartera, null, [],
                "No tienes ningún Tenant en tu cartera: no hay dónde ejecutar la orden.");

        var implicados = new HashSet<Guid>();
        if (tenantElegido is { } elegido)
        {
            if (candidatos.Tenants.All(t => t.TenantId != elegido))
                throw new ArgumentException("El Tenant elegido no es de la cartera.", nameof(tenantElegido));
            implicados.Add(elegido);
        }

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

        var enCartera = candidatos.Tenants.Where(t => implicados.Contains(t.TenantId)).ToList();

        return enCartera.Count switch
        {
            1 => new(SituacionTenantDestino.Unico, enCartera[0], enCartera,
                $"Todos los datos son de {enCartera[0].Nombre}."),
            > 1 => new(SituacionTenantDestino.Mezcla, null, enCartera,
                $"La orden junta datos de {string.Join(" y ", enCartera.Select(t => t.Nombre))}. " +
                "Una gestión se ejecuta en un solo Tenant: sepárala en una orden por Tenant."),
            _ when candidatos.Tenants.Count == 1 => new(SituacionTenantDestino.Unico, candidatos.Tenants[0], candidatos.Tenants,
                $"{candidatos.Tenants[0].Nombre} es el único Tenant de tu cartera."),
            _ => new(SituacionTenantDestino.Elegir, null, candidatos.Tenants,
                "La orden no dice en qué Tenant se trabaja. Elige uno de tu cartera."),
        };
    }
}
