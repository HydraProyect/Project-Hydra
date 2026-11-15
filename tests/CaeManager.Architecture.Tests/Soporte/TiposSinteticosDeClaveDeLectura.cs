using MediatR;

// Tipos de prueba de los controles sintéticos de la clave de LlamadaAlAlcanceDeCarteraEnLecturaTests. Existen solo para
// tener dos tipos homónimos cuyos nombres completos difieren únicamente por la raíz CaeManager.Application:
//   CaeManager.Application.ClaveSintetica.X   (relativo a Application: «ClaveSintetica.X»)
//   ClaveSintetica.X                          (externo: «global::ClaveSintetica.X»)
// Ninguna prueba los recorre como población de Application: viven en el ensamblado de tests, no en el de Application.

#pragma warning disable CA1812 // clases que solo se usan por reflexión

namespace CaeManager.Application.ClaveSintetica
{
    public sealed record PeticionSintetica : IRequest<int>;

    public sealed record PeticionGenerica<T> : IRequest<int>;

    public sealed class HandlerSintetico : IRequestHandler<PeticionSintetica, int>
    {
        public Task<int> Handle(PeticionSintetica request, CancellationToken cancellationToken) => Task.FromResult(0);
    }
}

namespace ClaveSintetica
{
    public sealed record PeticionSintetica : IRequest<int>;

    public sealed record PeticionGenerica<T> : IRequest<int>;

    public sealed class HandlerSintetico : IRequestHandler<PeticionSintetica, int>
    {
        public Task<int> Handle(PeticionSintetica request, CancellationToken cancellationToken) => Task.FromResult(0);
    }

    /// <summary>Handler genérico abierto: la petición que atiende, <c>PeticionGenerica&lt;T&gt;</c>, tiene FullName null.</summary>
    public sealed class HandlerGenerico<T> : IRequestHandler<PeticionGenerica<T>, int>
    {
        public Task<int> Handle(PeticionGenerica<T> request, CancellationToken cancellationToken) => Task.FromResult(0);
    }
}
