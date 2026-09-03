export function validateTokenLimits(
  contextTokens: number | null | undefined,
  maxOutputTokens: number | null | undefined,
  requireContext = false,
): string | null {
  if (requireContext && contextTokens == null) return '请填写模型上下文大小'
  if ([contextTokens, maxOutputTokens].some(value => value != null
    && (!Number.isInteger(value) || value <= 0 || value > 2147483647))) {
    return 'Token 限制必须是 1 到 2147483647 之间的整数'
  }
  if (contextTokens != null && maxOutputTokens != null && maxOutputTokens >= contextTokens) {
    return '最大输出 Token 必须小于上下文大小'
  }
  return null
}
