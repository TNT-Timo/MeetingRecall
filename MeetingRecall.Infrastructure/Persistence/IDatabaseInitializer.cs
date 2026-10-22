namespace MeetingRecall.Infrastructure.Persistence;

public interface IDatabaseInitializer
{
    void Initialize();

    void Reinitialize();
}
