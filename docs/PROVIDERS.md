# Provider & model

YuukaOCR only needs **one** provider to work. Any provider is configured in
*Settings → System*; keys live in `user/.env`.

## The three wire protocols

| Protocol | Endpoint | Used by |
| --- | --- | --- |
| `OpenAiCompatible` | `POST {base}/chat/completions` | 17 providers (local servers included) |
| `Gemini` | `POST {base}/models/{model}:generateContent` | Google Gemini |
| `Anthropic` | `POST {base}/messages` | Anthropic Claude |

Images are sent as a `data:` URI (OpenAI), `inline_data` (Gemini) or an `image` block (Anthropic).
PDF: Gemini/Anthropic accept them directly; OpenAI-compatible providers usually do not — the app then
sends the prompt with the file name and reports a clear error if the API refuses.

## The list

| Provider | Id | Protocol | Base URL | Env key | Vision | Suggested model |
| --- | --- | --- | --- | --- | --- | --- |
| Google Gemini | `gemini` | Gemini | `generativelanguage.googleapis.com/v1beta` | `GEMINI_API_KEY` | ✅ | `gemini-2.5-flash` |
| OpenAI | `openai` | OpenAI | `api.openai.com/v1` | `OPENAI_API_KEY` | ✅ | `gpt-4o-mini` |
| Anthropic Claude | `anthropic` | Anthropic | `api.anthropic.com/v1` | `ANTHROPIC_API_KEY` | ✅ | `claude-sonnet-5-5` |
| xAI Grok | `xai` | OpenAI | `api.x.ai/v1` | `XAI_API_KEY` | ✅ | `grok-4` |
| DeepSeek | `deepseek` | OpenAI | `api.deepseek.com/v1` | `DEEPSEEK_API_KEY` | ❌ | `deepseek-chat` |
| Alibaba Qwen (DashScope) | `qwen` | OpenAI | `dashscope-intl.aliyuncs.com/compatible-mode/v1` | `DASHSCOPE_API_KEY` | ✅ | `qwen-vl-max` |
| Moonshot Kimi | `moonshot` | OpenAI | `api.moonshot.ai/v1` | `MOONSHOT_API_KEY` | ✅ | `kimi-k2.5` |
| Z.ai / GLM | `zhipu` | OpenAI | `api.z.ai/api/paas/v4` | `ZAI_API_KEY` | ✅ | `glm-4.6v` |
| Mistral AI | `mistral` | OpenAI | `api.mistral.ai/v1` | `MISTRAL_API_KEY` | ✅ | `pixtral-12b-latest` |
| Groq | `groq` | OpenAI | `api.groq.com/openai/v1` | `GROQ_API_KEY` | ✅ | `meta-llama/llama-4-scout-17b-16e-instruct` |
| OpenRouter | `openrouter` | OpenAI | `openrouter.ai/api/v1` | `OPENROUTER_API_KEY` | ✅ | `google/gemini-2.5-flash` |
| Cerebras | `cerebras` | OpenAI | `api.cerebras.ai/v1` | `CEREBRAS_API_KEY` | ❌ | `llama-3.3-70b` |
| Together AI | `together` | OpenAI | `api.together.xyz/v1` | `TOGETHER_API_KEY` | ✅ | `meta-llama/Llama-4-Scout-17B-16E-Instruct` |
| Fireworks AI | `fireworks` | OpenAI | `api.fireworks.ai/inference/v1` | `FIREWORKS_API_KEY` | ✅ | `accounts/fireworks/models/qwen2p5-vl-32b-instruct` |
| NVIDIA NIM | `nvidia` | OpenAI | `integrate.api.nvidia.com/v1` | `NVIDIA_API_KEY` | ✅ | `meta/llama-4-maverick-17b-128e-instruct` |
| GitHub Models | `github` | OpenAI | `models.inference.ai.azure.com` | `GITHUB_API_KEY` | ✅ | `gpt-4o-mini` |
| HuggingFace Inference | `huggingface` | OpenAI | `api-inference.huggingface.co/v1` | `HUGGINGFACE_API_KEY` | ✅ | `Qwen/Qwen2.5-VL-72B-Instruct` |
| Ollama (local) | `ollama` | OpenAI | `localhost:11434/v1` | — | ✅ | `qwen2.5vl:7b` |
| LM Studio (local) | `lmstudio` | OpenAI | `localhost:1234/v1` | — | ✅ | `local-model` |
| Custom (OpenAI-compatible) | `custom` | OpenAI | `*(enter your own)*` | `CUSTOM_API_KEY` | ✅ | *(enter your own)* |

> The table above is a **suggestion**. The real model list comes from the provider's `GET /models`:
> it loads itself in the System tab as soon as the configured key answers (no button to press;
> *Refresh* re-reads it by hand), and the app sorts vision / free models first. On OpenRouter the
> `@preset/...` entries are always listed above every model — they are not part of `/models`, so they
> come from the models you have used before.

## Notes

* **Several keys**: append `_2`, `_3`... (`OPENAI_API_KEY_2`). The app rotates them to avoid rate limits.
* **Local servers**: Ollama / LM Studio / vLLM need no key — the app skips the key check.
* **Custom**: for vLLM, LiteLLM, internal gateways... enter the Base URL (ending in `/v1`).
* **The default models in the table are only suggestions**; if a provider renames a model, just type
  or pick another one in the model box — no code change needed.
