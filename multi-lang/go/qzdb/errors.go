package qzdb

import "errors"

// ErrorCode 对应 API_CONTRACT.md §7 的错误枚举。
// 注意：Go 的查询语义（未命中/非法 IP）统一返回 (nil, nil)，
// 因此这些错误码主要服务于构造期 Fail-Closed 与低级错误分类。
type ErrorCode int

const (
	// ErrCodeNotFound 未命中。
	ErrCodeNotFound ErrorCode = iota
	// ErrCodeCorrupted 数据损坏（CRC 不匹配 / 截断 / 越界）。
	ErrCodeCorrupted
	// ErrCodeOutOfBounds 越界访问。
	ErrCodeOutOfBounds
	// ErrCodeInvalidParam 非法参数。
	ErrCodeInvalidParam
	// ErrCodeBadHeader 头部非法。
	ErrCodeBadHeader
	// ErrCodeBadMagic 魔术字非法。
	ErrCodeBadMagic
	// ErrCodeUnsupported 不支持的版本 / 格式。
	ErrCodeUnsupported
)

// String 返回错误码名称。
func (c ErrorCode) String() string {
	switch c {
	case ErrCodeNotFound:
		return "NOT_FOUND"
	case ErrCodeCorrupted:
		return "CORRUPTED"
	case ErrCodeOutOfBounds:
		return "OUT_OF_BOUNDS"
	case ErrCodeInvalidParam:
		return "INVALID_PARAM"
	case ErrCodeBadHeader:
		return "BAD_HEADER"
	case ErrCodeBadMagic:
		return "BAD_MAGIC"
	case ErrCodeUnsupported:
		return "UNSUPPORTED"
	default:
		return "UNKNOWN"
	}
}

// QzdbError 携带 ErrorCode 的结构化错误（构造期 Fail-Closed 使用）。
type QzdbError struct {
	code ErrorCode
	Msg  string
}

// Error 实现 error 接口。
func (e *QzdbError) Error() string {
	if e.Msg != "" {
		return e.Msg
	}
	return e.code.String()
}

// Code 返回错误码。
func (e *QzdbError) Code() ErrorCode { return e.code }

func newErr(code ErrorCode, msg string) *QzdbError {
	return &QzdbError{code: code, Msg: msg}
}

// 通用错误哨兵（保持与旧版 / cmd 兼容）。
var (
	ErrNotFound    = errors.New("not found")
	ErrCorrupted   = errors.New("corrupted data")
	ErrOutOfBounds = errors.New("out of bounds")
	ErrInvalidParam = errors.New("invalid parameter")
	ErrBadHeader   = errors.New("bad header")
	ErrBadMagic    = errors.New("bad magic")
	ErrUnsupported = errors.New("unsupported format")
	// ErrClosed 表示 Reader 已关闭。
	ErrClosed = errors.New("qzdb reader is closed")
)
