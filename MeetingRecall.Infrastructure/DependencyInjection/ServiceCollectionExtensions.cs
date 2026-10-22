using Microsoft.Extensions.DependencyInjection;
using MeetingRecall.Core.Backup;
using MeetingRecall.Core.Interfaces;
using MeetingRecall.Core.Intelligence;
using MeetingRecall.Core.Models;
using MeetingRecall.Core.Settings;
using MeetingRecall.Core.Transcription;
using MeetingRecall.Infrastructure.Audio;
using MeetingRecall.Infrastructure.Backup;
using MeetingRecall.Infrastructure.Intelligence;
using MeetingRecall.Infrastructure.Models;
using MeetingRecall.Infrastructure.Persistence;
using MeetingRecall.Infrastructure.Repositories;
using MeetingRecall.Infrastructure.Settings;
using MeetingRecall.Infrastructure.Storage;
using MeetingRecall.Infrastructure.Transcription;

namespace MeetingRecall.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMeetingRecallInfrastructure(
        this IServiceCollection services,
        string? rootDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(new AppDataPaths(rootDirectory));
        services.AddSingleton<ISqliteConnectionFactory, SqliteConnectionFactory>();
        services.AddSingleton<IDatabaseInitializer, SqliteDatabaseInitializer>();
        services.AddSingleton<IConversationRepository, SqliteConversationRepository>();
        services.AddSingleton<ConversationDeletionService>();
        services.AddSingleton<IMeetingSummaryRepository, SqliteMeetingSummaryRepository>();
        services.AddSingleton<IConversationQuestionHistoryRepository,
            SqliteConversationQuestionHistoryRepository>();
        services.AddSingleton<ISpeakerRepository, SqliteSpeakerRepository>();
        services.AddSingleton<IConversationGroupRepository, SqliteConversationGroupRepository>();
        services.AddSingleton<IAudioArchiveRepository, SqliteAudioArchiveRepository>();
        services.AddSingleton<AudioMixdownService>();
        services.AddSingleton<INativeAudioRecordingService, NativeAudioRecordingService>();
        services.AddSingleton<IAudioTranscriptionNormalizer, AudioTranscriptionNormalizer>();
        services.AddSingleton<WhisperModelManager>();
        services.AddSingleton<IWhisperTranscriptionEngine, WhisperTranscriptionEngine>();
        services.AddSingleton<SpeakerDiarizationModelManager>();
        services.AddSingleton<INeuralSpeakerDiarizationEngine, SherpaOnnxSpeakerDiarizationEngine>();
        services.AddSingleton<SpeakerVoiceprintStore>();
        services.AddSingleton<ISpeakerDiarizationService, SpeakerDiarizationService>();
        services.AddSingleton<ILocalTranscriptionService, LocalTranscriptionService>();
        services.AddSingleton<SummaryLanguageModelManager>();
        services.AddSingleton<ILocalLanguageModelService, GpuFirstLocalLanguageModelService>();
        services.AddSingleton<LocalMeetingIntelligenceService>();
        services.AddSingleton<ILocalMeetingIntelligenceService>(provider =>
        {
            LocalMeetingIntelligenceService grounded =
                provider.GetRequiredService<LocalMeetingIntelligenceService>();
            ILocalLanguageModelService model =
                provider.GetRequiredService<ILocalLanguageModelService>();

            // The final whole-transcript editorial pass depends on Qwen's GPU worker and its
            // scoped thinking mode. Unit/integration tests frequently replace the language model
            // with deterministic fakes that intentionally provide only the legacy extraction and
            // synthesis responses; keep those fakes on the grounded pipeline instead of adding an
            // unexpected third inference. Production still resolves the editorial wrapper.
            if (model is not GpuFirstLocalLanguageModelService)
            {
                return grounded;
            }

            return new GlobalEditorialMeetingIntelligenceService(
                grounded,
                provider.GetRequiredService<IConversationRepository>(),
                provider.GetRequiredService<ISpeakerRepository>(),
                provider.GetRequiredService<IMeetingSummaryRepository>(),
                model,
                provider.GetRequiredService<SummaryLanguageModelManager>());
        });
        services.AddSingleton<ILocalConversationQuestionService, GroundedConversationQuestionService>();
        services.AddSingleton<ILocalModelBundleService, LocalModelBundleService>();
        services.AddSingleton<IApplicationSettingsService, ApplicationSettingsService>();
        services.AddSingleton<IApplicationDataPortabilityService, ApplicationDataPortabilityService>();

        return services;
    }
}
